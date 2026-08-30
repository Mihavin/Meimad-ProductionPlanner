[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$HostName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateSet('Reset', 'EStop', 'SingleBlock', 'BlockDelete', 'ModeChange')]
    [string]$Scenario = 'Reset',

    [ValidateRange(1, 65535)]
    [int]$DprntPort = 8080,

    [ValidatePattern('^[A-Z0-9-]+$')]
    [string]$MachineLabel = 'HAAS-VF3SS',

    [ValidateRange(15000, 600000)]
    [int]$MinimumElapsedMs = 15000,

    [ValidateRange(5, 600)]
    [int]$CaptureSeconds = 45,

    [ValidateRange(250, 30000)]
    [int]$ReadTimeoutMilliseconds = 500,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-DprntLineUntil {
    param(
        [Parameter(Mandatory = $true)][IO.StreamReader]$Reader,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Deadline
    )

    # StreamReader.ReadLine can remain blocked beyond NetworkStream.ReadTimeout.
    # Poll the asynchronous operation so the quiet-window evidence has a hard end.
    $readTask = $Reader.ReadLineAsync()
    while (-not $readTask.IsCompleted -and [DateTimeOffset]::UtcNow -lt $Deadline) {
        Start-Sleep -Milliseconds 100
    }

    if (-not $readTask.IsCompleted) { return $null }
    if ($readTask.IsFaulted) { throw $readTask.Exception.GetBaseException() }

    $line = $readTask.GetAwaiter().GetResult()
    if ($null -eq $line) { throw 'The Haas closed the DPRNT connection.' }
    return $line
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($resolvedOutput) -and -not $Force) {
    throw "Evidence file already exists. Choose a new run number or pass -Force: $resolvedOutput"
}
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must include a directory.'
}
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$expectsNoEvidence = $Scenario -in @('Reset', 'EStop', 'ModeChange')
$tcp = [Net.Sockets.TcpClient]::new()
$reader = $null
try {
    $tcp.Connect($HostName, $DprntPort)
    $stream = $tcp.GetStream()
    $stream.ReadTimeout = $ReadTimeoutMilliseconds
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)

    $ping = [Text.Encoding]::ASCII.GetBytes("ping`r`n")
    $stream.Write($ping, 0, $ping.Length)
    $stream.Flush()
    $pingReply = Read-DprntLineUntil -Reader $reader -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(10))
    if ($null -eq $pingReply -or $pingReply.Trim() -ne 'pingret') {
        throw "Expected Haas pingret before Test C, received: $pingReply"
    }

    $startedAt = [DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText(
        $resolvedOutput,
        "# Test C $Scenario DPRNT evidence started $($startedAt.ToString('O'))`r`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host 'CONNECTED AND PING VERIFIED.' -ForegroundColor Green
    switch ($Scenario) {
        'Reset' {
            Write-Host 'Run a fresh O01981. At M109, press RESET before entering any character.' -ForegroundColor Cyan
        }
        'EStop' {
            Write-Host 'Run a fresh O01981. At M109, press E-STOP before entering any character, then use the approved recovery.' -ForegroundColor Cyan
        }
        'ModeChange' {
            Write-Host 'Run a fresh O01981. At M109, attempt only the site-approved mode change; do not enter a character.' -ForegroundColor Cyan
        }
        'SingleBlock' {
            Write-Host 'Enable Single Block before Cycle Start. Run a fresh O01981; advance deliberately, wait 20 seconds at M109, then enter 7 once.' -ForegroundColor Cyan
        }
        'BlockDelete' {
            Write-Host 'Enable Block Delete before Cycle Start. Run a fresh O01981; wait 20 seconds at M109, then enter 7 once.' -ForegroundColor Cyan
        }
    }
    if ($expectsNoEvidence) {
        Write-Host "This recorder will pass after $CaptureSeconds seconds only if no Test-B finalizer evidence arrives." -ForegroundColor Cyan
    } else {
        Write-Host 'This recorder will require exactly one G65FINALIZER followed by one FINALIZERRETURNED.' -ForegroundColor Cyan
    }

    $escapedMachine = [regex]::Escape($MachineLabel)
    $finalizerPattern = '^MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/' + $escapedMachine +
        '/STARTMS/\s*(?<start>-?[0-9]+)/ENDMS/\s*(?<end>-?[0-9]+)/ELAPSEDMS/\s*(?<elapsed>-?[0-9]+)/INPUT/\s*(?<input>-?[0-9]+)\s*$'
    $returnedPattern = '^MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/' + $escapedMachine + '\s*$'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($CaptureSeconds)
    $finalizerSeen = $false
    $returnedSeen = $false

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $line = Read-DprntLineUntil -Reader $reader -Deadline $deadline
        if ($null -eq $line) { break }

        $receivedAt = [DateTimeOffset]::UtcNow
        [IO.File]::AppendAllText(
            $resolvedOutput,
            "$($receivedAt.ToString('O'))`t$line`r`n",
            [Text.UTF8Encoding]::new($false))
        Write-Host "DPRNT: $line"

        if ($expectsNoEvidence) {
            throw "Unexpected DPRNT record during Test C $Scenario. The interrupted program must not emit finalizer/return evidence."
        }

        $finalizerMatch = [regex]::Match($line, $finalizerPattern)
        if ($finalizerMatch.Success) {
            if ($finalizerSeen) { throw 'Duplicate G65FINALIZER record received.' }
            $elapsed = [int64]$finalizerMatch.Groups['elapsed'].Value
            $inputCode = [int]$finalizerMatch.Groups['input'].Value
            if ($elapsed -lt $MinimumElapsedMs) {
                throw "G65FINALIZER elapsed time $elapsed ms is below the required $MinimumElapsedMs ms."
            }
            if ($inputCode -ne 55) {
                throw "G65FINALIZER input code $inputCode is not ASCII 55 for digit 7."
            }
            $finalizerSeen = $true
            continue
        }

        if ([regex]::IsMatch($line, $returnedPattern)) {
            if (-not $finalizerSeen) { throw 'FINALIZERRETURNED arrived before G65FINALIZER.' }
            if ($returnedSeen) { throw 'Duplicate FINALIZERRETURNED record received.' }
            $returnedSeen = $true
            break
        }

        throw "Unexpected DPRNT line during isolated Test C ${Scenario}: $line"
    }

    if ($expectsNoEvidence) {
        Write-Host ''
        Write-Host "TEST C $Scenario DPRNT PASS: no finalizer or return record was received." -ForegroundColor Green
        Write-Host "Evidence: $resolvedOutput" -ForegroundColor Green
        Write-Host 'Confirm on the control that the program stopped and #10500 is empty.' -ForegroundColor Cyan
        return
    }

    if (-not $finalizerSeen -or -not $returnedSeen) {
        throw "Test C $Scenario capture ended without exactly one finalizer and one return record."
    }

    Write-Host ''
    Write-Host "TEST C $Scenario DPRNT PASS: exactly one finalizer and one return record were captured." -ForegroundColor Green
    Write-Host "Evidence: $resolvedOutput" -ForegroundColor Green
    Write-Host 'Confirm on the control that O01981 reached M30 and #10500 is empty.' -ForegroundColor Cyan
}
finally {
    if ($null -ne $reader) { $reader.Dispose() }
    $tcp.Dispose()
}
