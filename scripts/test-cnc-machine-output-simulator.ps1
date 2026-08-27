$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools\Meimad.Planner.CncSimulator\Meimad.Planner.CncSimulator.csproj'
$scenario = Join-Path $root 'tools\Meimad.Planner.CncSimulator\scenario.verification-commissioning.json'
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("meimad-cnc-output-" + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    & dotnet run --project $project -- --scenario $scenario --output $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Machine-output simulator returned a nonzero exit code.' }
    $bytes = [System.IO.File]::ReadAllBytes($temporary)
    if ($bytes | Where-Object { $_ -gt 127 }) { throw 'Transcript is not ASCII.' }
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    if ($text -notmatch "`r`n" -or $text -match "(?<!`r)`n") {
        throw 'Transcript must use CRLF line endings.'
    }
    $lines = $text.Split(@("`r`n"), [StringSplitOptions]::RemoveEmptyEntries)
    if ($lines.Count -ne 10) { throw "Expected 10 delivered lines, found $($lines.Count)." }
    if (($lines | Where-Object { $_ -eq $lines[5] }).Count -ne 3) {
        throw 'The repeated cycle-end event was not delivered exactly three times.'
    }
    foreach ($line in $lines) {
        if ($line -notmatch '^MEIMAD/V/1/EVENT/[A-Z]{3}/ID/[A-Z0-9-]+/SEQ/[0-9]+/MACROVERSION/[0-9]+') {
            throw "Malformed strict transcript line: $line"
        }
    }

    $ErrorActionPreference = 'Continue'
    & dotnet run --project $project --no-build -- --scenario $scenario --output $temporary *> $null
    $refusalExitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($refusalExitCode -eq 0) { throw 'Simulator unexpectedly overwrote a transcript without --force.' }
    Write-Host 'CNC Machine-output simulator tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
