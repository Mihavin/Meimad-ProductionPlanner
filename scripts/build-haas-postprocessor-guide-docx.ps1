[CmdletBinding()]
param(
    [string] $SourcePath,
    [string] $OutputPath,
    [string] $XpsPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Join-Path $repositoryRoot 'docs\haas-ngc-postprocessor-and-macro-specification.md' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repositoryRoot 'docs\Meimad-Haas-NGC-Postprocessor-Programmer-Guide-v1.7.docx' }
if ([string]::IsNullOrWhiteSpace($XpsPath)) { $XpsPath = Join-Path $repositoryRoot '.diagnostics\postprocessor-guide-v1.7.xps' }
$source = (Resolve-Path -LiteralPath $SourcePath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$xps = [IO.Path]::GetFullPath($XpsPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($xps)) | Out-Null

function Clean-Inline([string] $text) {
    $value = $text -replace '\*\*', '' -replace '`', ''
    return $value -replace '\[([^\]]+)\]\([^\)]+\)', '$1'
}

$word = New-Object -ComObject Word.Application
$word.Visible = $false
$word.DisplayAlerts = 0
try {
    $doc = $word.Documents.Add()
    $section = $doc.Sections.Item(1)
    $section.PageSetup.PageWidth = $word.InchesToPoints(8.5)
    $section.PageSetup.PageHeight = $word.InchesToPoints(11)
    $section.PageSetup.TopMargin = $word.InchesToPoints(0.8)
    $section.PageSetup.BottomMargin = $word.InchesToPoints(0.75)
    $section.PageSetup.LeftMargin = $word.InchesToPoints(0.85)
    $section.PageSetup.RightMargin = $word.InchesToPoints(0.85)
    $section.PageSetup.HeaderDistance = $word.InchesToPoints(0.35)
    $section.PageSetup.FooterDistance = $word.InchesToPoints(0.35)

    $normal = $doc.Styles.Item('Normal')
    $normal.Font.Name = 'Calibri'
    $normal.Font.Size = 11
    $normal.ParagraphFormat.SpaceAfter = 6
    $normal.ParagraphFormat.LineSpacingRule = 5
    $normal.ParagraphFormat.LineSpacing = 13.75

    foreach ($styleName in @('Heading 1','Heading 2','Heading 3')) {
        $style = $doc.Styles.Item($styleName)
        $style.Font.Name = 'Calibri'
        $style.Font.Bold = $true
        $style.Font.Color = 11891758
        $style.ParagraphFormat.KeepWithNext = $true
    }
    $doc.Styles.Item('Heading 1').Font.Size = 16
    $doc.Styles.Item('Heading 1').ParagraphFormat.SpaceBefore = 18
    $doc.Styles.Item('Heading 1').ParagraphFormat.SpaceAfter = 10
    $doc.Styles.Item('Heading 2').Font.Size = 13
    $doc.Styles.Item('Heading 2').ParagraphFormat.SpaceBefore = 14
    $doc.Styles.Item('Heading 2').ParagraphFormat.SpaceAfter = 7
    $doc.Styles.Item('Heading 3').Font.Size = 12
    $doc.Styles.Item('Heading 3').ParagraphFormat.SpaceBefore = 10
    $doc.Styles.Item('Heading 3').ParagraphFormat.SpaceAfter = 5

    $header = $section.Headers.Item(1).Range
    $header.Text = 'MEIMAD  |  HAAS NGC POSTPROCESSOR GUIDE'
    $header.Font.Name = 'Calibri'
    $header.Font.Size = 8.5
    $header.Font.Color = 8421504
    $footer = $section.Footers.Item(1).Range
    $footer.ParagraphFormat.Alignment = 2
    $footer.Font.Name = 'Calibri'
    $footer.Font.Size = 8.5
    $footer.Text = 'Meimad Production Planner  |  '
    $footer.Collapse(0)
    $footer.Fields.Add($footer, -1, 'PAGE', $true) | Out-Null

    $selection = $word.Selection
    $lines = [IO.File]::ReadAllLines($source)
    $inCode = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^```') { $inCode = -not $inCode; continue }
        if ($inCode) {
            $selection.Range.ListFormat.RemoveNumbers()
            $selection.Style = $normal
            $selection.Font.Name = 'Consolas'
            $selection.Font.Size = 9
            $selection.ParagraphFormat.SpaceAfter = 0
            $selection.ParagraphFormat.LineSpacingRule = 0
            $selection.Shading.BackgroundPatternColor = 15987699
            $selection.TypeText($line)
            $selection.TypeParagraph()
            continue
        }
        $selection.Shading.BackgroundPatternColor = -16777216
        if ($line -match '^# (.+)$') {
            $selection.Range.ListFormat.RemoveNumbers()
            $selection.Style = $normal
            $selection.Font.Name = 'Calibri'
            $selection.Font.Size = 28
            $selection.Font.Bold = $true
            $selection.Font.Color = 7884063
            $selection.ParagraphFormat.SpaceAfter = 6
            $selection.ParagraphFormat.KeepWithNext = $true
            $selection.TypeText((Clean-Inline $Matches[1]))
            $selection.TypeParagraph()
            $selection.Font.Size = 12
            $selection.Font.Bold = $false
            $selection.Font.Color = 6908265
            $selection.ParagraphFormat.SpaceAfter = 18
            $selection.TypeText('Source-template markers, package-build rules, SolidCAM GPPL, Cimatron GPP and GPP2 examples')
            $selection.TypeParagraph()
            continue
        }
        if ($line -match '^## (.+)$') { $selection.Range.ListFormat.RemoveNumbers(); $selection.Style = $doc.Styles.Item('Heading 1'); $selection.TypeText((Clean-Inline $Matches[1])); $selection.TypeParagraph(); continue }
        if ($line -match '^### (.+)$') { $selection.Range.ListFormat.RemoveNumbers(); $selection.Style = $doc.Styles.Item('Heading 2'); $selection.TypeText((Clean-Inline $Matches[1])); $selection.TypeParagraph(); continue }
        if ($line -match '^#### (.+)$') { $selection.Range.ListFormat.RemoveNumbers(); $selection.Style = $doc.Styles.Item('Heading 3'); $selection.TypeText((Clean-Inline $Matches[1])); $selection.TypeParagraph(); continue }
        if ($line -match '^\|.+\|$' -and $i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\|[-: |]+\|$') {
            $rows = New-Object Collections.Generic.List[object]
            $headers = @($line.Trim('|').Split('|') | ForEach-Object { (Clean-Inline $_.Trim()) })
            $i += 2
            while ($i -lt $lines.Count -and $lines[$i] -match '^\|.+\|$') {
                $rows.Add(@($lines[$i].Trim('|').Split('|') | ForEach-Object { (Clean-Inline $_.Trim()) }))
                $i++
            }
            $i--
            $table = $doc.Tables.Add($selection.Range, $rows.Count + 1, $headers.Count)
            $table.AllowAutoFit = $false
            $table.Borders.Enable = 1
            $width = $word.InchesToPoints(6.8) / $headers.Count
            for ($column = 1; $column -le $headers.Count; $column++) {
                $table.Columns.Item($column).Width = $width
                $table.Cell(1,$column).Range.Text = $headers[$column - 1]
                $table.Cell(1,$column).Range.Font.Bold = $true
                $table.Cell(1,$column).Shading.BackgroundPatternColor = 15132390
            }
            for ($row = 0; $row -lt $rows.Count; $row++) {
                for ($column = 0; $column -lt $headers.Count; $column++) {
                    $table.Cell($row + 2,$column + 1).Range.Text = if ($column -lt $rows[$row].Count) { $rows[$row][$column] } else { '' }
                }
            }
            $table.Range.Font.Name = 'Calibri'
            $table.Range.Font.Size = 9.5
            $table.Rows.Item(1).HeadingFormat = $true
            $selection.SetRange($table.Range.End, $table.Range.End)
            $selection.TypeParagraph()
            continue
        }
        if ($line -match '^- \[ \] (.+)$') {
            $itemText = $Matches[1]
            while ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s{2,}(\S.*)$') { $i++; $itemText += ' ' + $Matches[1].Trim() }
            $selection.Style = $normal
            $selection.Range.ListFormat.ApplyBulletDefault()
            $selection.TypeText('[ ] ' + (Clean-Inline $itemText))
            $selection.TypeParagraph()
            continue
        }
        if ($line -match '^- (.+)$') {
            $itemText = $Matches[1]
            while ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s{2,}(\S.*)$') { $i++; $itemText += ' ' + $Matches[1].Trim() }
            $selection.Style = $normal
            $selection.Range.ListFormat.ApplyBulletDefault()
            $selection.TypeText((Clean-Inline $itemText))
            $selection.TypeParagraph()
            continue
        }
        if ($line -match '^\d+\. (.+)$') {
            $itemText = $Matches[1]
            while ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s{2,}(\S.*)$') { $i++; $itemText += ' ' + $Matches[1].Trim() }
            $selection.Style = $normal
            $selection.Range.ListFormat.ApplyNumberDefault()
            $selection.TypeText((Clean-Inline $itemText))
            $selection.TypeParagraph()
            continue
        }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $paragraphText = $line.Trim()
        while (($i + 1 -lt $lines.Count) -and (-not [string]::IsNullOrWhiteSpace($lines[$i + 1])) -and ($lines[$i + 1] -notmatch '^(#|```|\|.+\|$|- |\d+\. )')) {
            $i++
            $paragraphText += ' ' + $lines[$i].Trim()
        }
        $selection.Range.ListFormat.RemoveNumbers()
        $selection.Style = $normal
        $selection.Font.Name = 'Calibri'
        $selection.Font.Size = 11
        $selection.ParagraphFormat.SpaceAfter = 6
        $selection.ParagraphFormat.LineSpacingRule = 5
        $selection.ParagraphFormat.LineSpacing = 13.75
        $selection.TypeText((Clean-Inline $paragraphText))
        $selection.TypeParagraph()
    }

    $selection.Range.ListFormat.RemoveNumbers()
    $doc.SaveAs2($output, 16)
    $doc.ExportAsFixedFormat($xps, 18)
    $doc.Close($false)
}
finally {
    $word.Quit()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($word) | Out-Null
}

Write-Output $output
Write-Output $xps
