param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..\client-windows\Meimad.Planner.Client.Windows'),
    [string]$ServerRoot = (Join-Path $PSScriptRoot '..\server\Meimad.Planner.Server')
)

$ErrorActionPreference = 'Stop'
$attributePattern = '(?:Text|Content|Header|Title|ToolTip|AutomationProperties\.Name|AutomationProperties\.HelpText)="([^"]+)"'
$strings = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
Get-ChildItem -LiteralPath $ProjectRoot -Recurse -Filter '*.xaml' | ForEach-Object {
    $raw = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    [regex]::Matches($raw, $attributePattern) | ForEach-Object {
        $value = [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value).Trim()
        if ($value -and !$value.StartsWith('{') -and $value -notmatch '^#|^[0-9,.* ]+$|^[A-Z]:\\') {
            [void]$strings.Add($value)
        }
    }
    $withoutComments = [regex]::Replace($raw, '<!--[\s\S]*?-->', '')
    [regex]::Matches($withoutComments, '>([^<]+)<') | ForEach-Object {
        $value = [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value).Trim()
        if ($value -and $value -notmatch '^\{|^#|^[0-9,.* ]+$' -and $value -match '[A-Za-z]') {
            [void]$strings.Add($value)
        }
    }
}

$codeRoots = @(
    (Join-Path $ProjectRoot 'Presentation'),
    (Join-Path $ProjectRoot 'Views'),
    (Join-Path $ProjectRoot 'Formatting'),
    (Join-Path $ProjectRoot 'MainWindow.xaml.cs'),
    (Join-Path $ServerRoot 'Api'),
    (Join-Path $ServerRoot 'Application'),
    (Join-Path $ServerRoot 'Domain'),
    (Join-Path $ServerRoot 'Configuration'))
Get-ChildItem -Path $codeRoots -Recurse -File -Filter '*.cs' | ForEach-Object {
    $raw = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    [regex]::Matches($raw, '(?<!")"((?:\\.|[^"\\])*)"(?!")') | ForEach-Object {
        try { $value = [regex]::Unescape($_.Groups[1].Value).Trim() } catch { $value = '' }
        if ($value.Length -ge 3 -and $value.Length -le 500 -and $value -match '[A-Za-z]' -and $value -notmatch '[\r\n]' -and $value -notmatch '^/|^https?://|SELECT |INSERT |UPDATE |DELETE FROM |api/v1|^[a-z0-9_.:/-]+$') {
            [void]$strings.Add($value)
        }
    }
}

function Invoke-BatchTranslation([string[]]$source, [string]$target) {
    $result = [ordered]@{}
    $separator = '[[[MEIMAD_SPLIT_42]]]'
    $batches = [System.Collections.Generic.List[object]]::new()
    $current = [System.Collections.Generic.List[string]]::new()
    $length = 0
    foreach ($value in $source) {
        if ($current.Count -gt 0 -and $length + $value.Length -gt 3200) {
            $batches.Add($current.ToArray())
            $current = [System.Collections.Generic.List[string]]::new()
            $length = 0
        }
        $current.Add($value)
        $length += $value.Length + $separator.Length + 2
    }
    if ($current.Count -gt 0) { $batches.Add($current.ToArray()) }

    foreach ($batch in $batches) {
        $joined = $batch -join "`n$separator`n"
        $uri = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=' + $target + '&dt=t&q=' + [uri]::EscapeDataString($joined)
        $response = Invoke-RestMethod -Uri $uri -TimeoutSec 60
        $translated = (($response[0] | ForEach-Object { $_.Item(0) }) -join '') -split "\s*\[\[\[MEIMAD_SPLIT_42\]\]\]\s*"
        if ($translated.Count -ne $batch.Count) {
            throw "Translation batch split mismatch for $target ($($batch.Count) source, $($translated.Count) result)."
        }
        for ($index = 0; $index -lt $batch.Count; $index++) {
            $result[$batch[$index]] = $translated[$index].Trim()
        }
    }
    return $result
}

$ordered = @($strings) | Sort-Object
$outputDirectory = Join-Path $ProjectRoot 'Localization'
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
foreach ($language in @('he', 'ru')) {
    $catalog = Invoke-BatchTranslation $ordered $language
    $json = $catalog | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText(
        (Join-Path $outputDirectory "strings.$language.json"),
        $json,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Generated $($ordered.Count) strings for Hebrew and Russian."
