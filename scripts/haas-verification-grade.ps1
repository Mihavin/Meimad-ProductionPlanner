[CmdletBinding(DefaultParameterSetName = 'Path')]
param(
    [ValidateSet('Vectors', 'Identity')]
    [string]$Mode = 'Vectors',

    [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
    [ValidateNotNullOrEmpty()]
    [string]$InputPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Lines')]
    [AllowEmptyCollection()]
    [AllowEmptyString()]
    [string[]]$InputLines
)

$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -eq 'Path') {
    $resolvedInput = [System.IO.Path]::GetFullPath($InputPath)
    if (-not [System.IO.File]::Exists($resolvedInput)) {
        throw "Input file does not exist: $resolvedInput"
    }
    $InputLines = [System.IO.File]::ReadAllLines($resolvedInput)
}

if ($Mode -eq 'Identity') {
    $identityExpected = [ordered]@{
        '1' = 123401
        '2' = 432101
        '4' = 123501
    }
    $identityPattern = '^MEIMADSPIKE/CASE/(?<case>[124])/PROBE/9010/IDENTITY/(?<identity>[0-9]{6})$'
    $identityCounts = [ordered]@{ '1' = 0; '2' = 0; '4' = 0 }
    $identityObservations = [System.Collections.Generic.List[object]]::new()
    $identityLineCount = 0
    foreach ($lineValue in $InputLines) {
        $line = if ($null -eq $lineValue) { '' } else { $lineValue.Trim() }
        if (-not $line.StartsWith('MEIMADSPIKE/CASE/', [StringComparison]::Ordinal)) { continue }
        $identityLineCount++
        $match = [regex]::Match($line, $identityPattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $match.Success) {
            $identityObservations.Add([ordered]@{ case = $null; passed = $false; code = 'malformed'; line = $line })
            continue
        }
        $case = $match.Groups['case'].Value
        $identity = [int]$match.Groups['identity'].Value
        $passed = $identity -eq $identityExpected[$case]
        if ($passed) { $identityCounts[$case]++ }
        $identityObservations.Add([ordered]@{
            case = $case
            passed = $passed
            code = if ($passed) { 'matched' } else { 'identity_mismatch' }
            expectedIdentity = $identityExpected[$case]
            actualIdentity = $identity
            line = $line
        })
    }
    $identityAttempted = $identityLineCount -gt 0
    $identityFailed = @($identityObservations | Where-Object { -not $_.passed })
    $requiredRepetitionsPerCase = 4
    $insufficient = @($identityExpected.Keys | Where-Object { $identityCounts[$_] -lt $requiredRepetitionsPerCase })
    $identityPassed = $identityAttempted -and $identityFailed.Count -eq 0 -and $insufficient.Count -eq 0
    [ordered]@{
        protocol = 'MEIMADSPIKE/CASE'
        status = if (-not $identityAttempted) { 'NOT_RUN' } elseif ($identityPassed) { 'PASS' } else { 'FAIL' }
        attempted = $identityAttempted
        requiredRepetitionsPerCase = $requiredRepetitionsPerCase
        matchedCounts = $identityCounts
        insufficientCases = $insufficient
        observations = $identityObservations
        allPassed = if ($identityAttempted) { $identityPassed } else { $null }
    } | ConvertTo-Json -Depth 8
    return
}

$expected = [ordered]@{
    V01 = [ordered]@{ nonce = 731841; offsetRelease = 483920; nc = 654321; digits = 6; response = '438513' }
    V02 = [ordered]@{ nonce = 731842; offsetRelease = 483920; nc = 654321; digits = 6; response = '286999' }
    V03 = [ordered]@{ nonce = 731841; offsetRelease = 483921; nc = 654321; digits = 6; response = '543409' }
    V04 = [ordered]@{ nonce = 731841; offsetRelease = 483920; nc = 654322; digits = 6; response = '953665' }
    V05 = [ordered]@{ nonce = 731841; offsetRelease = 483920; nc = 654321; digits = 6; response = '210076' }
    V06 = [ordered]@{ nonce = 100000; offsetRelease = 100000; nc = 100000; digits = 4; response = '0282' }
    V07 = [ordered]@{ nonce = 999999; offsetRelease = 999999; nc = 999999; digits = 5; response = '69667' }
}

$pattern = '^MEIMADSPIKE/V/1/TEST/(?<test>V[0-9]{2})/NONCE/(?<nonce>[0-9]{6})/OFFSETRELEASE/(?<offset>[0-9]{6})/NC/(?<nc>[0-9]{6})/DIGITS/(?<digits>[4-6])/RESPONSE/(?<response>[0-9]{4,6})$'
$observations = [System.Collections.Generic.List[object]]::new()
$seen = @{}
$vectorLineCount = 0

foreach ($lineValue in $InputLines) {
    $line = if ($null -eq $lineValue) { '' } else { $lineValue.Trim() }
    if (-not $line.StartsWith('MEIMADSPIKE/V/1/', [StringComparison]::Ordinal)) { continue }
    $vectorLineCount++
    $match = [regex]::Match($line, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        $observations.Add([ordered]@{ test = $null; passed = $false; code = 'malformed'; line = $line })
        continue
    }

    $test = $match.Groups['test'].Value
    if (-not $expected.Contains($test)) {
        $observations.Add([ordered]@{ test = $test; passed = $false; code = 'unexpected_test'; line = $line })
        continue
    }
    if ($seen.ContainsKey($test)) {
        $observations.Add([ordered]@{ test = $test; passed = $false; code = 'duplicate_test'; line = $line })
        continue
    }
    $seen[$test] = $true

    $actual = [ordered]@{
        nonce = [int]$match.Groups['nonce'].Value
        offsetRelease = [int]$match.Groups['offset'].Value
        nc = [int]$match.Groups['nc'].Value
        digits = [int]$match.Groups['digits'].Value
        response = $match.Groups['response'].Value
    }
    $wanted = $expected[$test]
    $passed = $actual.nonce -eq $wanted.nonce `
        -and $actual.offsetRelease -eq $wanted.offsetRelease `
        -and $actual.nc -eq $wanted.nc `
        -and $actual.digits -eq $wanted.digits `
        -and $actual.response -ceq $wanted.response
    $observations.Add([ordered]@{
        test = $test
        passed = $passed
        code = if ($passed) { 'matched' } else { 'vector_mismatch' }
        expected = $wanted
        actual = $actual
        line = $line
    })
}

$missing = @($expected.Keys | Where-Object { -not $seen.ContainsKey($_) })
$failed = @($observations | Where-Object { -not $_.passed })
$attempted = $vectorLineCount -gt 0
$allPassed = $attempted -and $missing.Count -eq 0 -and $failed.Count -eq 0
$result = [ordered]@{
    protocol = 'MEIMADSPIKE/V/1'
    status = if (-not $attempted) { 'NOT_RUN' } elseif ($allPassed) { 'PASS' } else { 'FAIL' }
    attempted = $attempted
    expectedCount = $expected.Count
    matchedCount = @($observations | Where-Object { $_.passed }).Count
    missingTests = $missing
    observations = $observations
    allPassed = if ($attempted) { $allPassed } else { $null }
}

$result | ConvertTo-Json -Depth 8
