<#
.SYNOPSIS
  Compares an original workbook with its cleaned copy in real Excel.

.DESCRIPTION
  Opens temporary copies of both files in a separate, hidden Excel instance and reports:
  open time, save time, time to copy/insert and delete rows, conditional-format rule
  counts, and whether the formatting Excel displays (DisplayFormat: font color, bold,
  fill) is identical for every non-empty cell. Neither input file is modified.

.EXAMPLE
  .\compare_in_excel.ps1 -Original "\\server\share\Working plane.xlsx" -Cleaned ".\Working plane - cleaned.xlsx"
#>
param(
    [Parameter(Mandatory = $true)] [string] $Original,
    [Parameter(Mandatory = $true)] [string] $Cleaned,
    [string] $WorkDir = (Join-Path $env:TEMP 'excel-cleanup-compare'),
    [switch] $SkipFormatCompare
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

function Test-Workbook([string] $Path, [string] $Label) {
    $copy = Join-Path $WorkDir "$Label.xlsx"
    Copy-Item -LiteralPath $Path -Destination $copy -Force
    $saved = Join-Path $WorkDir "$Label-saved.xlsx"
    if (Test-Path $saved) { Remove-Item $saved -Force }

    $existing = @(Get-Process EXCEL -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    $xl = New-Object -ComObject Excel.Application
    $ours = @(Get-Process EXCEL -ErrorAction SilentlyContinue | Where-Object { $existing -notcontains $_.Id } | ForEach-Object { $_.Id })
    if ($ours.Count -eq 0 -or $xl.Workbooks.Count -gt 0 -or $xl.ProtectedViewWindows.Count -gt 0) {
        # Excel handed us an instance the user already has open: never touch or quit it.
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($xl)
        throw 'Excel reused an already running instance. Close all Excel windows and run again.'
    }
    $r = [ordered]@{ File = $Label; SizeMB = [math]::Round((Get-Item $copy).Length / 1MB, 1) }
    try {
        $xl.Visible = $false; $xl.DisplayAlerts = $false; $xl.AskToUpdateLinks = $false
        $xl.ScreenUpdating = $false; $xl.EnableEvents = $false
        $sw = [Diagnostics.Stopwatch]::StartNew()

        $wb = $xl.Workbooks.Open($copy, 0, $false)
        $r.OpenSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        $r.Repaired = [bool]($wb.Name -match 'Repaired' -or $xl.Caption -match 'Repaired')

        $rules = 0
        foreach ($ws in $wb.Worksheets) { $rules += $ws.Cells.FormatConditions.Count }
        $r.CFRules = $rules

        if (-not $SkipFormatCompare) {
            $lines = New-Object System.Collections.Generic.List[string]
            foreach ($ws in $wb.Worksheets) {
                $ur = $ws.UsedRange
                $f = $ur.Formula
                if ($f -isnot [array]) { continue }
                $r0 = $ur.Row; $c0 = $ur.Column
                for ($i = 1; $i -le $f.GetLength(0); $i++) {
                    for ($j = 1; $j -le $f.GetLength(1); $j++) {
                        if ([string]$f[$i, $j] -eq '') { continue }
                        $d = $ws.Cells.Item($r0 + $i - 1, $c0 + $j - 1).DisplayFormat
                        $lines.Add("$($ws.Name)`tR$($r0 + $i - 1)C$($c0 + $j - 1)`t$($d.Font.Color)`t$($d.Font.Bold)`t$($d.Interior.Color)`t$($d.Interior.Pattern)")
                    }
                }
            }
            [IO.File]::WriteAllLines((Join-Path $WorkDir "$Label.display.txt"), $lines)
            $r.CellsDumped = $lines.Count
        }

        # Row operations on the first sheet, like a user copying a job row
        $ws = $wb.Worksheets.Item(1)
        $sw.Restart()
        for ($k = 0; $k -lt 3; $k++) {
            [void]$ws.Rows.Item(11).Insert()
            [void]$ws.Rows.Item(10).Copy($ws.Rows.Item(11))
        }
        $r.Copy3RowsSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        $sw.Restart()
        for ($k = 0; $k -lt 3; $k++) { [void]$ws.Rows.Item(11).Delete() }
        $r.Delete3RowsSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)

        $sw.Restart()
        $wb.SaveAs($saved, 51)
        $r.SaveSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        $wb.Close($false)
    }
    finally {
        $xl.Quit()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($xl)
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        Start-Sleep -Seconds 2
        foreach ($p in $ours) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
    }
    [pscustomobject]$r
}

$results = @(
    Test-Workbook -Path $Original -Label 'original'
    Test-Workbook -Path $Cleaned -Label 'cleaned'
)
$results | Format-Table -AutoSize | Out-String -Width 200

if (-not $SkipFormatCompare) {
    $a = [IO.File]::ReadAllLines((Join-Path $WorkDir 'original.display.txt'))
    $b = [IO.File]::ReadAllLines((Join-Path $WorkDir 'cleaned.display.txt'))
    $diff = @(Compare-Object $a $b)
    if ($diff.Count -eq 0) {
        "Displayed formatting: identical for all $($a.Count) non-empty cells."
    } else {
        "Displayed formatting: $($diff.Count) differing lines (<= original, => cleaned). First 20:"
        $diff | Select-Object -First 20 | Format-Table -AutoSize | Out-String -Width 200
    }
}
