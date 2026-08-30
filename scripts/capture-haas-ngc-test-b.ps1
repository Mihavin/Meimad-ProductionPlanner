[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$HostName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateRange(1, 65535)]
    [int]$DprntPort = 8080,

    [ValidatePattern('^[A-Z0-9-]+$')]
    [string]$MachineLabel = 'HAAS-VF3SS',

    [ValidateRange(15000, 600000)]
    [int]$MinimumElapsedMs = 15000,

    [ValidateRange(30, 600)]
    [int]$CaptureSeconds = 180,

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

    while ([DateTimeOffset]::UtcNow -lt $Deadline) {
        try {
            $line = $Reader.ReadLine()
            if ($null -eq $line) {
                throw 'The Haas closed the DPRNT connection.'
            }
            return $line
        }
        catch [IO.IOException] {
            if ([DateTimeOffset]::UtcNow -ge $Deadline) {
                break
            }
        }
    }

    throw 'No DPRNT line was received before the capture timeout.'
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

    $pingDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $pingReply = Read-DprntLineUntil -Reader $reader -Deadline $pingDeadline
    if ($pingReply.Trim() -ne 'pingret') {
        throw "Expected Haas pingret before Test B, received: $pingReply"
    }

    $startedAt = [DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText(
        $resolvedOutput,
        "# Test B DPRNT evidence started $($startedAt.ToString('O'))`r`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host 'CONNECTED AND PING VERIFIED.' -ForegroundColor Green
    Write-Host 'Run O01981 now. At M109 wait at least 20 seconds, then enter digit 7 once.' -ForegroundColor Cyan
    Write-Host 'The recorder will stop after G65FINALIZER and FINALIZERRETURNED arrive in order.'

    $escapedMachine = [regex]::Escape($MachineLabel)
    $finalizerPattern = '^MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/' + $escapedMachine +
        '/STARTMS/\s*(?<start>-?[0-9]+)/ENDMS/\s*(?<end>-?[0-9]+)/ELAPSEDMS/\s*(?<elapsed>-?[0-9]+)/INPUT/\s*(?<input>-?[0-9]+)\s*$'
    $returnedPattern = '^MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/' + $escapedMachine + '\s*$'
    $captureDeadline = [DateTimeOffset]::UtcNow.AddSeconds($CaptureSeconds)
    $finalizerSeen = $false

    while ([DateTimeOffset]::UtcNow -lt $captureDeadline) {
        $line = Read-DprntLineUntil -Reader $reader -Deadline $captureDeadline
        $receivedAt = [DateTimeOffset]::UtcNow
        [IO.File]::AppendAllText(
            $resolvedOutput,
            "$($receivedAt.ToString('O'))`t$line`r`n",
            [Text.UTF8Encoding]::new($false))
        Write-Host "DPRNT: $line"

        $finalizerMatch = [regex]::Match($line, $finalizerPattern)
        if ($finalizerMatch.Success) {
            if ($finalizerSeen) {
                throw 'A duplicate G65FINALIZER record was received.'
            }

            $elapsed = [int64]$finalizerMatch.Groups['elapsed'].Value
            $inputCode = [int]$finalizerMatch.Groups['input'].Value
            if ($elapsed -lt $MinimumElapsedMs) {
                throw "G65FINALIZER elapsed time $elapsed ms is below the required $MinimumElapsedMs ms. Test B failed."
            }
            if ($inputCode -ne 55) {
                throw "G65FINALIZER input code $inputCode is not ASCII 55 for digit 7. Test B failed."
            }

            $finalizerSeen = $true
            continue
        }

        if ([regex]::IsMatch($line, $returnedPattern)) {
            if (-not $finalizerSeen) {
                throw 'FINALIZERRETURNED arrived before G65FINALIZER. Test B failed.'
            }

            Write-Host ''
            Write-Host 'TEST B DPRNT PASS: both records were captured in order.' -ForegroundColor Green
            Write-Host "Evidence: $resolvedOutput" -ForegroundColor Green
            Write-Host 'Confirm on the control that O01981 reached M30 and #10500 is empty.' -ForegroundColor Cyan
            return
        }

        throw "Unexpected DPRNT line during isolated Test B: $line"
    }

    throw 'Test B capture timed out before both required DPRNT records arrived.'
}
finally {
    if ($null -ne $reader) {
        $reader.Dispose()
    }
    $tcp.Dispose()
}
