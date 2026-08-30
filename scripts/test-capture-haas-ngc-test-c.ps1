$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$recorder = Join-Path $PSScriptRoot 'capture-haas-ngc-test-c.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot ('.diagnostics\haas-ngc-test-c-recorder-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-FakeHaas {
    param([int]$Port, [string[]]$Lines, [int]$KeepOpenSeconds = 1)

    $payload = [string]::Join([char]30, $Lines)
    $job = Start-Job -ScriptBlock {
        param($Port, $Payload, $KeepOpenSeconds)
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
                foreach ($line in $Payload.Split([char]30)) {
                    if (-not [string]::IsNullOrEmpty($line)) { $writer.WriteLine($line) }
                }
                Start-Sleep -Seconds $KeepOpenSeconds
            }
            finally { $client.Dispose() }
        }
        finally { $listener.Stop() }
    } -ArgumentList $Port, $payload, $KeepOpenSeconds

    while ($job.State -eq 'NotStarted' -or -not ($job.ChildJobs[0].Output -contains 'READY')) {
        if ($job.State -in @('Failed', 'Stopped', 'Completed')) { throw "Fake Haas did not start: $(Receive-Job $job -Keep)" }
        Start-Sleep -Milliseconds 20
    }
    return $job
}

function Stop-FakeHaas {
    param([Management.Automation.Job]$Job)
    if ($null -eq $Job) { return }
    Wait-Job $Job -Timeout 10 | Out-Null
    if ($Job.State -eq 'Failed') { Receive-Job $Job -ErrorAction Stop | Out-Null }
    Remove-Job $Job -Force
}

$finalizer = 'MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/HAAS-VF3SS/STARTMS/  100000/ENDMS/  120500/ELAPSEDMS/   20500/INPUT/ 55'
$returned = 'MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/HAAS-VF3SS'

$port = Get-FreeTcpPort
$job = Start-FakeHaas -Port $port -Lines @($finalizer, $returned)
try {
    $output = Join-Path $testRoot 'single-block.txt'
    & $recorder -HostName '127.0.0.1' -DprntPort $port -Scenario SingleBlock `
        -OutputPath $output -CaptureSeconds 5 -ReadTimeoutMilliseconds 250
    $saved = Get-Content -LiteralPath $output -Raw
    if ($saved -notmatch 'Test C SingleBlock' -or $saved -notmatch 'G65FINALIZER' -or $saved -notmatch 'FINALIZERRETURNED') {
        throw 'Test C recorder did not preserve valid Single Block evidence.'
    }
}
finally { Stop-FakeHaas $job }

$port = Get-FreeTcpPort
$job = Start-FakeHaas -Port $port -Lines @() -KeepOpenSeconds 6
try {
    $output = Join-Path $testRoot 'reset.txt'
    & $recorder -HostName '127.0.0.1' -DprntPort $port -Scenario Reset `
        -OutputPath $output -CaptureSeconds 5 -ReadTimeoutMilliseconds 250
    $saved = Get-Content -LiteralPath $output -Raw
    if ($saved -notmatch 'Test C Reset') { throw 'Test C recorder did not save quiet Reset evidence.' }
}
finally { Stop-FakeHaas $job }

$port = Get-FreeTcpPort
$job = Start-FakeHaas -Port $port -Lines @($finalizer)
$rejected = $false
try {
    & $recorder -HostName '127.0.0.1' -DprntPort $port -Scenario EStop `
        -OutputPath (Join-Path $testRoot 'estop-unexpected.txt') -CaptureSeconds 5 -ReadTimeoutMilliseconds 250
}
catch { $rejected = $_.Exception.Message -match 'Unexpected DPRNT record' }
finally { Stop-FakeHaas $job }
if (-not $rejected) { throw 'Test C recorder accepted finalizer evidence during an E-stop interruption.' }

Write-Host 'Haas NGC Test C recorder checks passed.'
