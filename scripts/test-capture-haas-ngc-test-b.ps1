$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$recorder = Join-Path $PSScriptRoot 'capture-haas-ngc-test-b.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot ('.diagnostics\haas-ngc-test-b-recorder-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-FakeHaas {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string[]]$Lines
    )

    $serializedLines = [string]::Join([char]30, $Lines)
    $job = Start-Job -ScriptBlock {
        param($Port, $SerializedLines)
        $Lines = $SerializedLines.Split([char]30)
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        try {
            'READY'
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
                $writer = [IO.StreamWriter]::new($stream, [Text.Encoding]::ASCII, 4096, $true)
                $writer.NewLine = "`r`n"
                $writer.AutoFlush = $true
                if ($reader.ReadLine() -ne 'ping') { throw 'Recorder did not send ping.' }
                $writer.WriteLine('pingret')
                foreach ($line in $Lines) { $writer.WriteLine($line) }
            }
            finally { $client.Dispose() }
        }
        finally { $listener.Stop() }
    } -ArgumentList $Port, $serializedLines

    while ($job.State -eq 'NotStarted' -or -not ($job.ChildJobs[0].Output -contains 'READY')) {
        if ($job.State -in @('Failed', 'Stopped', 'Completed')) {
            throw "Fake Haas did not start: $(Receive-Job $job -Keep)"
        }
        Start-Sleep -Milliseconds 20
    }
    return $job
}

function Stop-FakeHaas {
    param([Management.Automation.Job]$Job)
    if ($null -eq $Job) { return }
    Wait-Job $Job -Timeout 5 | Out-Null
    if ($Job.State -eq 'Failed') { Receive-Job $Job -ErrorAction Stop | Out-Null }
    Remove-Job $Job -Force
}

$validLines = @(
    'MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/HAAS-VF3SS/STARTMS/  100000/ENDMS/  120500/ELAPSEDMS/   20500/INPUT/ 55',
    'MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/HAAS-VF3SS')

$port = Get-FreeTcpPort
$job = Start-FakeHaas -Port $port -Lines $validLines
try {
    $validOutput = Join-Path $testRoot 'valid.txt'
    & $recorder -HostName '127.0.0.1' -DprntPort $port -OutputPath $validOutput `
        -CaptureSeconds 30 -ReadTimeoutMilliseconds 250
    $saved = Get-Content -LiteralPath $validOutput -Raw
    foreach ($line in $validLines) {
        if ($saved.IndexOf($line, [StringComparison]::Ordinal) -lt 0) {
            throw "Valid Test B evidence did not preserve: $line"
        }
    }
}
finally { Stop-FakeHaas $job }

$port = Get-FreeTcpPort
$job = Start-FakeHaas -Port $port -Lines @($validLines[1], $validLines[0])
$rejected = $false
try {
    & $recorder -HostName '127.0.0.1' -DprntPort $port `
        -OutputPath (Join-Path $testRoot 'wrong-order.txt') `
        -CaptureSeconds 30 -ReadTimeoutMilliseconds 250
}
catch { $rejected = $_.Exception.Message -match 'before G65FINALIZER' }
finally { Stop-FakeHaas $job }
if (-not $rejected) { throw 'Test B recorder accepted reversed records.' }

$shortLine = $validLines[0] -replace '20500', '14999'
$port = Get-FreeTcpPort
$job = Start-FakeHaas -Port $port -Lines @($shortLine, $validLines[1])
$rejected = $false
try {
    & $recorder -HostName '127.0.0.1' -DprntPort $port `
        -OutputPath (Join-Path $testRoot 'too-short.txt') `
        -CaptureSeconds 30 -ReadTimeoutMilliseconds 250
}
catch { $rejected = $_.Exception.Message -match 'below the required' }
finally { Stop-FakeHaas $job }
if (-not $rejected) { throw 'Test B recorder accepted elapsed time below 15000 ms.' }

Write-Host 'Haas NGC Test B recorder checks passed.'
