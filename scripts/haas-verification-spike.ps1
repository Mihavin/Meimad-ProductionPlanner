[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$MachineLabel,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$HostName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateRange(1, 65535)]
    [int]$MdcPort = 5051,

    [ValidateRange(1, 65535)]
    [int]$DprntPort = 8080,

    [ValidateRange(10, 3600)]
    [int]$CaptureSeconds = 120,

    [ValidateRange(250, 30000)]
    [int]$TimeoutMilliseconds = 3000,

    [ValidateRange(1, 99999)]
    [int[]]$CandidateReadOnlyVariables = @(),

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$toolVersion = '1.1.0'

function Invoke-ReadOnlyMdcQuery {
    param(
        [Parameter(Mandatory = $true)][string]$Query
    )

    if ($Query -notmatch '^\?Q(?:101|102|500)$' -and $Query -notmatch '^\?Q600 [0-9]{1,5}$') {
        throw "Unsafe MDC query rejected: $Query"
    }

    $tcpClient = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $tcpClient.ConnectAsync($HostName, $MdcPort)
        if (-not $connectTask.Wait($TimeoutMilliseconds)) {
            throw "MDC connection timed out."
        }
        $null = $connectTask.GetAwaiter().GetResult()
        $networkStream = $tcpClient.GetStream()
        $networkStream.ReadTimeout = $TimeoutMilliseconds
        $networkStream.WriteTimeout = $TimeoutMilliseconds
        $requestBytes = [System.Text.Encoding]::ASCII.GetBytes("$Query`n")
        $networkStream.Write($requestBytes, 0, $requestBytes.Length)
        $networkStream.Flush()
        $reader = [System.IO.StreamReader]::new(
            $networkStream,
            [System.Text.Encoding]::ASCII,
            $false,
            1024,
            $true)
        try {
            return $reader.ReadLine()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $tcpClient.Dispose()
    }
}

function Read-MdcSnapshot {
    $queries = [ordered]@{
        softwareVersion = '?Q101'
        machineModel = '?Q102'
        activeProgram = '?Q500'
    }
    $responses = [ordered]@{}
    foreach ($entry in $queries.GetEnumerator()) {
        try {
            $responses[$entry.Key] = [ordered]@{
                query = $entry.Value
                response = Invoke-ReadOnlyMdcQuery -Query $entry.Value
                error = $null
            }
        }
        catch {
            $responses[$entry.Key] = [ordered]@{
                query = $entry.Value
                response = $null
                error = $_.Exception.Message
            }
        }
    }

    $candidateResponses = @()
    foreach ($variable in $CandidateReadOnlyVariables) {
        $query = "?Q600 $variable"
        try {
            $candidateResponses += [ordered]@{
                variable = $variable
                query = $query
                response = Invoke-ReadOnlyMdcQuery -Query $query
                error = $null
            }
        }
        catch {
            $candidateResponses += [ordered]@{
                variable = $variable
                query = $query
                response = $null
                error = $_.Exception.Message
            }
        }
    }

    return [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        queries = $responses
        candidateVariables = $candidateResponses
    }
}

function Receive-DprntLines {
    $lines = [System.Collections.Generic.List[object]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()
    $tcpClient = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $tcpClient.ConnectAsync($HostName, $DprntPort)
        if (-not $connectTask.Wait($TimeoutMilliseconds)) {
            throw "DPRNT connection timed out."
        }
        $null = $connectTask.GetAwaiter().GetResult()
        $networkStream = $tcpClient.GetStream()
        $networkStream.ReadTimeout = 500
        $reader = [System.IO.StreamReader]::new(
            $networkStream,
            [System.Text.Encoding]::ASCII,
            $false,
            4096,
            $true)
        try {
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds($CaptureSeconds)
            Write-Host "DPRNT capture connected. Run only the approved non-cutting identity/vector probe now."
            while ([DateTimeOffset]::UtcNow -lt $deadline -and $lines.Count -lt 10000) {
                try {
                    $line = $reader.ReadLine()
                    if ($null -eq $line) {
                        $errors.Add('DPRNT peer closed the connection.')
                        break
                    }
                    $lines.Add([ordered]@{
                        receivedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
                        line = $line
                    })
                }
                catch [System.IO.IOException] {
                    if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
                }
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    catch {
        $errors.Add($_.Exception.Message)
    }
    finally {
        $tcpClient.Dispose()
    }

    return [ordered]@{
        lines = $lines
        errors = $errors
        truncated = $lines.Count -ge 10000
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
if ([System.IO.File]::Exists($resolvedOutput) -and -not $Force) {
    throw "Output already exists. Choose a new path or pass -Force: $resolvedOutput"
}
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must include a directory.'
}
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

Write-Warning 'This tool performs read-only MDC queries and passive DPRNT capture only.'
Write-Warning 'Do not include the Machine secret, response variable, or nonce variable in CandidateReadOnlyVariables.'
$startedAt = [DateTimeOffset]::UtcNow
$before = Read-MdcSnapshot
$dprnt = Receive-DprntLines
$after = Read-MdcSnapshot
$completedAt = [DateTimeOffset]::UtcNow
$graderPath = Join-Path $PSScriptRoot 'haas-verification-grade.ps1'
$capturedLines = @($dprnt.lines | ForEach-Object { $_.line })
$vectorGrade = (& $graderPath -Mode Vectors -InputLines $capturedLines) | ConvertFrom-Json
$identityGrade = (& $graderPath -Mode Identity -InputLines $capturedLines) | ConvertFrom-Json

$evidence = [ordered]@{
    schemaVersion = 1
    toolVersion = $toolVersion
    machineLabel = $MachineLabel
    networkAddressStored = $false
    mdcPort = $MdcPort
    dprntPort = $DprntPort
    startedAtUtc = $startedAt.ToString('O')
    completedAtUtc = $completedAt.ToString('O')
    captureSecondsRequested = $CaptureSeconds
    safety = [ordered]@{
        mdcWritesPermitted = $false
        cncProgramTransferPerformed = $false
        planningMutationPerformed = $false
    }
    before = $before
    dprnt = $dprnt
    responseVectorGrade = $vectorGrade
    identityTransportGrade = $identityGrade
    after = $after
    operatorRecord = [ordered]@{
        controllerSoftware = $null
        protectedProgramNumber = $null
        callerProgramNumber = $null
        candidateIdentityVariable = $null
        setting23Protected = $null
        powerCycleResult = $null
        resetResult = $null
        conclusion = 'UNREVIEWED'
        reviewedBy = $null
        reviewedAtUtc = $null
        notes = $null
    }
}

$json = $evidence | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText($resolvedOutput, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "Evidence written to $resolvedOutput"
