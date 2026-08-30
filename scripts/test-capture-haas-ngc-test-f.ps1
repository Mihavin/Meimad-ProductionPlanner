$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$recorder = Join-Path $PSScriptRoot 'capture-haas-ngc-test-f.ps1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot ('.diagnostics\haas-ngc-test-f-recorder-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-FakeHaas {
    param([int]$Port, [string[]]$Lines)
    $payload = [string]::Join([char]30, $Lines)
    $job = Start-Job -ScriptBlock {
        param($Port, $Payload)
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
                foreach ($line in $Payload.Split([char]30)) { $writer.WriteLine($line) }
            }
            finally { $client.Dispose() }
        }
        finally { $listener.Stop() }
    } -ArgumentList $Port, $payload
    while ($job.State -eq 'NotStarted' -or -not ($job.ChildJobs[0].Output -contains 'READY')) {
        if ($job.State -in @('Failed', 'Stopped', 'Completed')) { throw "Fake Haas did not start: $(Receive-Job $job -Keep)" }
        Start-Sleep -Milliseconds 20
    }
    return $job
}

function Stop-FakeHaas {
    param([Management.Automation.Job]$Job)
    Wait-Job $Job -Timeout 5 | Out-Null
    if ($Job.State -eq 'Failed') { Receive-Job $Job -ErrorAction Stop | Out-Null }
    Remove-Job $Job -Force
}

$direct = 'MEIMADENG/V/1/TEST/M109DIRECT/MACHINE/HAAS-VF3SS/STARTMS/  100000/ENDMS/  120500/ELAPSEDMS/   20500/INPUT/ 55'
$finalizer = 'MEIMADENG/V/1/TEST/G65FINALIZER/MACHINE/HAAS-VF3SS/STARTMS/  200000/ENDMS/  220500/ELAPSEDMS/   20500/INPUT/ 55'
$returned = 'MEIMADENG/V/1/TEST/FINALIZERRETURNED/MACHINE/HAAS-VF3SS'

foreach ($case in @(
    @{ RecordType = 'DirectO1980'; Lines = @($direct); File = 'before-direct.txt'; Marker = 'M109DIRECT' },
    @{ RecordType = 'FinalizerO1982'; Lines = @($finalizer, $returned); File = 'after-finalizer.txt'; Marker = 'G65FINALIZER' })) {
    $port = Get-FreeTcpPort
    $job = Start-FakeHaas -Port $port -Lines $case.Lines
    try {
        $output = Join-Path $testRoot $case.File
        & $recorder -HostName '127.0.0.1' -DprntPort $port -RecordType $case.RecordType `
            -Phase BeforeReboot -OutputPath $output -CaptureSeconds 30
        $saved = Get-Content -LiteralPath $output -Raw
        if ($saved -notmatch $case.Marker) { throw "Test F recorder did not save $($case.Marker)." }
    }
    finally { Stop-FakeHaas $job }
}

Write-Host 'Haas NGC Test F recorder checks passed.'
