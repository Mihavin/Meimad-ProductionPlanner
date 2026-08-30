[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$HostName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateSet('DirectO1980', 'FinalizerO1982')]
    [string]$RecordType = 'DirectO1980',

    [ValidateSet('BeforeReboot', 'AfterReboot')]
    [string]$Phase = 'BeforeReboot',

    [ValidateRange(1, 65535)]
    [int]$DprntPort = 8080,

    [ValidatePattern('^[A-Z0-9-]+$')]
    [string]$MachineLabel = 'HAAS-VF3SS',

    [ValidateRange(15000, 600000)]
    [int]$MinimumElapsedMs = 15000,

    [ValidateRange(30, 600)]
    [int]$CaptureSeconds = 180,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-DprntLineUntil {
    param(
        [Parameter(Mandatory = $true)][IO.StreamReader]$Reader,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Deadline
    )

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

function Save-EvidenceLine {
    param([string]$Path, [string]$Line)
    [IO.File]::AppendAllText(
        $Path,
        "$([DateTimeOffset]::UtcNow.ToString('O'))`t$Line`r`n",
        [Text.UTF8Encoding]::new($false))
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($resolvedOutput) -and -not $Force) {
    throw "Evidence file already exists. Choose a new filename or pass -Force: $resolvedOutput"
}
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) { throw 'OutputPath must include a directory.' }
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$tcp = [Net.Sockets.TcpClient]::new()
$reader = $null
try {
    $tcp.Connect($HostName, $DprntPort)
    $stream = $tcp.GetStream()
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
    $ping = [Text.Encoding]::ASCII.GetBytes("ping`r`n")
    $stream.Write($ping, 0, $ping.Length)
    $stream.Flush()
    $pingReply = Read-DprntLineUntil -Reader $reader -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(10))
    if ($null -eq $pingReply -or $pingReply.Trim() -ne 'pingret') {
        throw "Expected Haas pingret before Test F, received: $pingReply"
    }

    [IO.File]::WriteAllText(
        $resolvedOutput,
        "# Test F $Phase $RecordType timer evidence started $([DateTimeOffset]::UtcNow.ToString('O'))`r`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host 'CONNECTED AND PING VERIFIED.' -ForegroundColor Green
    if ($RecordType -eq 'DirectO1980') {
        Write-Host 'Run O01980. At M109 wait at least 20 seconds, then enter digit 7 once.' -ForegroundColor Cyan
    } else {
        Write-Host 'Run O01981 (it calls O01982). At M109 wait at least 20 seconds, then enter digit 7 once.' -ForegroundColor Cyan
    }
    Write-Host "This is the $Phase timer observation. It records values; it does not infer reboot behavior." -ForegroundColor Cyan

    $escapedMachine = [regex]::Escape($MachineLabel)
    $directPattern = '^MEIMADENG/V/1/TEST/M109DIRECT/MACHINE/' + $escapedMachine +
        '/STARTMS/\s*(?<start>-?[0-9]+)/ENDMS/\s*(?<end>-?[0-9]+)/ELAPSEDMS/\s*(?<elapsed>-?[0-9]+)/INPUT/\s*(?<input>-?[0-9]+)\s*$'
    $finalizerPattern = '^MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/' + $escapedMachine +
        '/STARTMS/\s*(?<start>-?[0-9]+)/ENDMS/\s*(?<end>-?[0-9]+)/ELAPSEDMS/\s*(?<elapsed>-?[0-9]+)/INPUT/\s*(?<input>-?[0-9]+)\s*$'
    $returnedPattern = '^MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/' + $escapedMachine + '\s*$'
    $timerPattern = if ($RecordType -eq 'DirectO1980') { $directPattern } else { $finalizerPattern }
    $timerLabel = if ($RecordType -eq 'DirectO1980') { 'M109DIRECT' } else { 'G65FINALIZER' }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($CaptureSeconds)
    $timerSeen = $false

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $line = Read-DprntLineUntil -Reader $reader -Deadline $deadline
        if ($null -eq $line) { break }
        Save-EvidenceLine -Path $resolvedOutput -Line $line
        Write-Host "DPRNT: $line"

        $timerMatch = [regex]::Match($line, $timerPattern)
        if ($timerMatch.Success) {
            if ($timerSeen) { throw "Duplicate $timerLabel record received." }
            $elapsed = [int64]$timerMatch.Groups['elapsed'].Value
            $inputCode = [int]$timerMatch.Groups['input'].Value
            if ($elapsed -lt $MinimumElapsedMs) { throw "$timerLabel elapsed time $elapsed ms is below $MinimumElapsedMs ms." }
            if ($inputCode -ne 55) { throw "$timerLabel input code $inputCode is not ASCII 55 for digit 7." }
            $timerSeen = $true
            if ($RecordType -eq 'DirectO1980') { break }
            continue
        }

        if ($RecordType -eq 'FinalizerO1982' -and [regex]::IsMatch($line, $returnedPattern)) {
            if (-not $timerSeen) { throw 'FINALIZERRETURNED arrived before G65FINALIZER.' }
            break
        }

        throw "Unexpected DPRNT line during Test F $Phase ${RecordType}: $line"
    }

    if (-not $timerSeen) { throw "Test F $Phase $RecordType ended without the required $timerLabel record." }
    Write-Host ''
    Write-Host "TEST F $Phase $RecordType CAPTURED." -ForegroundColor Green
    Write-Host "Evidence: $resolvedOutput" -ForegroundColor Green
    Write-Host 'Confirm the called program reached M30 and #10500 is empty.' -ForegroundColor Cyan
}
finally {
    if ($null -ne $reader) { $reader.Dispose() }
    $tcp.Dispose()
}
