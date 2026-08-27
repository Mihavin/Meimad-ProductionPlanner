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

    [ValidateRange(100000, 999999)]
    [int]$ExpectedNcIdentity = 654321,

    [ValidateRange(100000, 999999)]
    [int]$ExpectedOffsetReleaseToken = 483920,

    [ValidateRange(100000, 999999)]
    [int]$TestMachineKey = 271828,

    [ValidateRange(1, 999999)]
    [int]$ExpectedMacroVersion = 5,

    [ValidateRange(30, 600)]
    [int]$CaptureSeconds = 240,

    [ValidateRange(250, 30000)]
    [int]$TimeoutMilliseconds = 5000,

    [switch]$AllowExpiredChallengeObservation,

    [switch]$AllowCompetingLocalDprntClient,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-BenchResponseCode {
    param(
        [Parameter(Mandatory = $true)][int]$Nonce,
        [Parameter(Mandatory = $true)][int]$OffsetReleaseToken,
        [Parameter(Mandatory = $true)][int]$NcIdentity,
        [Parameter(Mandatory = $true)][int]$MachineKey
    )

    $symbols = [System.Collections.Generic.List[int]]::new()
    $symbols.Add(1)
    foreach ($value in @($Nonce, $OffsetReleaseToken, $NcIdentity, $MachineKey, 314159)) {
        foreach ($character in $value.ToString('D6', [Globalization.CultureInfo]::InvariantCulture).ToCharArray()) {
            $symbols.Add([int][char]::GetNumericValue($character))
        }
    }

    $state = 7919
    foreach ($symbol in $symbols) {
        $state = ($state % 90909) * 11 + $symbol
    }

    return ($state % 1000000).ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($resolvedOutput) -and -not $Force) {
    throw "Output already exists. Choose a new path or pass -Force: $resolvedOutput"
}

$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must include a directory.'
}
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[IO.File]::WriteAllText(
    $resolvedOutput,
    "# Meimad Haas live bench capture started $([DateTimeOffset]::UtcNow.ToString('O'))`r`n",
    [Text.UTF8Encoding]::new($false))

Write-Warning 'BENCH ONLY: public test key; never use this helper or key for production.'
Write-Host 'This helper is a passive DPRNT client. It cannot write to or control the CNC.'

if (-not $AllowCompetingLocalDprntClient -and (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
    $resolvedAddresses = @([Net.Dns]::GetHostAddresses($HostName) | ForEach-Object IPAddressToString)
    $competing = @(Get-NetTCPConnection -RemotePort $DprntPort -State Established -ErrorAction SilentlyContinue |
        Where-Object { $_.RemoteAddress -in $resolvedAddresses })
    if ($competing.Count -gt 0) {
        $owners = ($competing | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
        throw "Another local process already owns an established Haas DPRNT connection (PID: $owners). Use the Server as the sole production reader; do not run competing capture clients."
    }
}

$client = [Net.Sockets.TcpClient]::new()
try {
    $connectTask = $client.ConnectAsync($HostName, $DprntPort)
    if (-not $connectTask.Wait($TimeoutMilliseconds)) {
        throw 'DPRNT connection timed out.'
    }
    $null = $connectTask.GetAwaiter().GetResult()

    $stream = $client.GetStream()
    $stream.ReadTimeout = 500
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
    try {
        Write-Host ''
        Write-Host 'CONNECTED. Run O01991 once now.' -ForegroundColor Cyan
        Write-Host 'Do not run O01990 until a six-digit RESPONSE is displayed.'

        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($CaptureSeconds)
        $challengeReceivedAt = $null
        $responseDisplayed = $false
        $successSeen = $false

        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $line = $reader.ReadLine()
                if ($null -eq $line) {
                    throw 'The Haas closed the DPRNT connection.'
                }

                $receivedAt = [DateTimeOffset]::UtcNow
                $olcPattern = '^MEIMAD/V/1/EVENT/OLC/.*/MACROVERSION/(?<macro>[0-9]+)/PROGRAM/(?<program>[0-9]{6})/OFFSETRELEASE/(?<release>[0-9]{6})/NONCE/(?<nonce>[0-9]{6})$'
                $olcMatch = [regex]::Match($line, $olcPattern)
                $loggedLine = if ($olcMatch.Success) {
                    [regex]::Replace($line, '/NONCE/[0-9]{6}$', '/NONCE/REDACTED')
                } else { $line }
                [IO.File]::AppendAllText(
                    $resolvedOutput,
                    "$($receivedAt.ToString('O'))`t$loggedLine`r`n",
                    [Text.UTF8Encoding]::new($false))
                Write-Host "DPRNT: $loggedLine"

                if ($olcMatch.Success) {
                    if ($responseDisplayed) {
                        throw 'A second OLC challenge arrived in the same capture. Stop and start a clean session.'
                    }
                    $macroVersion = [int]$olcMatch.Groups['macro'].Value
                    $program = [int]$olcMatch.Groups['program'].Value
                    $release = [int]$olcMatch.Groups['release'].Value
                    $nonce = [int]$olcMatch.Groups['nonce'].Value
                    if ($macroVersion -ne $ExpectedMacroVersion -or
                        $program -ne $ExpectedNcIdentity -or $release -ne $ExpectedOffsetReleaseToken) {
                        throw "Unexpected challenge context: macro $macroVersion, program $program, release $release. Do not run O01990."
                    }

                    $response = Get-BenchResponseCode `
                        -Nonce $nonce `
                        -OffsetReleaseToken $release `
                        -NcIdentity $program `
                        -MachineKey $TestMachineKey
                    $challengeReceivedAt = $receivedAt
                    $responseDisplayed = $true
                    Write-Host ''
                    Write-Host "RESPONSE: $response" -ForegroundColor Green
                    Write-Host 'Run O01990 now. Enter one digit per M109 prompt, pressing WRITE/ENTER after each digit.' -ForegroundColor Cyan
                    Write-Host ''
                    continue
                }

                if ($line -match "^MEIMAD/V/1/EVENT/SVS/.*/MACROVERSION/$ExpectedMacroVersion/PROGRAM/$ExpectedNcIdentity$") {
                    $successSeen = $true
                    Write-Host 'SVS RECEIVED: verification succeeded.' -ForegroundColor Green
                    continue
                }

                if ($line -eq "MEIMADSPIKE/NC/$ExpectedNcIdentity/VERIFICATION/RETURNED") {
                    if (-not $successSeen) {
                        throw 'Return marker arrived without a preceding SVS record.'
                    }
                    Write-Host 'STEP 5 PASS: SVS and verification return were both captured.' -ForegroundColor Green
                    return
                }

                if ($line -match '^MEIMAD/V/1/EVENT/SVF/') {
                    throw 'SVF RECEIVED: verification failed. Record the CNC alarm and do not reuse this challenge.'
                }
            }
            catch [IO.IOException] {
                if (-not $AllowExpiredChallengeObservation -and
                    $null -ne $challengeReceivedAt -and
                    ([DateTimeOffset]::UtcNow - $challengeReceivedAt).TotalSeconds -gt 120) {
                    throw 'The captured challenge exceeded 120 seconds. Run O01991 again for a fresh challenge.'
                }
            }
        }

        if (-not $responseDisplayed) {
            throw 'Capture ended without a valid OLC challenge. O01990 must not be run.'
        }
        throw 'Capture ended without both SVS and the verification return marker.'
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $client.Dispose()
    Write-Host "Capture log: $resolvedOutput"
}
