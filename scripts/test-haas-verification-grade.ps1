[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$grader = Join-Path $PSScriptRoot 'haas-verification-grade.ps1'
$vectorCalculator = Join-Path $PSScriptRoot 'haas-verification-vector.ps1'
$fixtures = Join-Path $repositoryRoot 'tests\fixtures'

function Read-Grade([string]$fileName) {
    $path = Join-Path $fixtures $fileName
    return ((& $grader -InputPath $path) | ConvertFrom-Json)
}

function Read-IdentityGrade([string]$fileName) {
    $path = Join-Path $fixtures $fileName
    return ((& $grader -Mode Identity -InputPath $path) | ConvertFrom-Json)
}

$pass = Read-Grade 'haas-verification-vectors-pass.txt'
if ($pass.status -ne 'PASS' -or -not $pass.allPassed -or $pass.matchedCount -ne 7) {
    throw 'Expected the complete published-vector fixture to pass.'
}

$fail = Read-Grade 'haas-verification-vectors-fail.txt'
if ($fail.status -ne 'FAIL' -or
    $fail.allPassed -or
    $fail.observations[0].code -ne 'vector_mismatch') {
    throw 'Expected the mismatched response fixture to fail closed.'
}

$identity = Read-Grade 'haas-verification-identity-only.txt'
if ($identity.status -ne 'NOT_RUN' -or
    $identity.attempted -or
    $null -ne $identity.allPassed) {
    throw 'Expected an identity-only capture to leave vector grading not run.'
}

$identityPass = Read-IdentityGrade 'haas-verification-identity-pass.txt'
if ($identityPass.status -ne 'PASS' -or -not $identityPass.allPassed -or
    $identityPass.requiredRepetitionsPerCase -ne 4 -or
    $identityPass.matchedCounts.'1' -ne 4 -or
    $identityPass.matchedCounts.'2' -ne 4 -or
    $identityPass.matchedCounts.'4' -ne 4) {
    throw 'Expected four correct repetitions of every identity case to pass.'
}

$identityShort = Read-IdentityGrade 'haas-verification-identity-only.txt'
if ($identityShort.status -ne 'FAIL' -or $identityShort.allPassed -or
    $identityShort.matchedCounts.'1' -ne 1 -or
    $identityShort.insufficientCases.Count -ne 3) {
    throw 'Expected an incomplete identity capture to fail closed.'
}

$identityNotRun = Read-IdentityGrade 'haas-verification-vectors-pass.txt'
if ($identityNotRun.status -ne 'NOT_RUN' -or $identityNotRun.attempted -or
    $null -ne $identityNotRun.allPassed) {
    throw 'Expected vector-only input to leave identity grading not run.'
}

$candidateRoot = Join-Path $repositoryRoot 'HaasVF3-NC_Example'
$runnerPath = Join-Path $candidateRoot '9012.CNC'
$calculatorPath = Join-Path $candidateRoot '9013.CNC'
$runnerLines = [System.IO.File]::ReadAllLines($runnerPath)
$callPattern = '^G65 P9013 A(?<nonce>[0-9]{6})\. B(?<offset>[0-9]{6})\. C(?<nc>[0-9]{6})\. D(?<key>[0-9]{6})\. E(?<digits>[4-6])\.$'
$calls = @($runnerLines | Where-Object { $_ -match $callPattern })
if ($calls.Count -ne 7) { throw 'Expected exactly seven public-vector calls in O09012.' }

for ($index = 0; $index -lt $calls.Count; $index++) {
    if ($calls[$index] -notmatch $callPattern) { throw 'Unexpected vector call syntax.' }
    $calculated = (& $vectorCalculator `
        -Nonce ([int]$Matches.nonce) `
        -OffsetReleaseToken ([int]$Matches.offset) `
        -NcIdentityToken ([int]$Matches.nc) `
        -TestMachineKey ([int]$Matches.key) `
        -ResponseDigits ([int]$Matches.digits)) | ConvertFrom-Json
    $expectedId = 'V{0:D2}' -f ($index + 1)
    $fixtureObservation = @($pass.observations | Where-Object { $_.test -eq $expectedId })
    if ($fixtureObservation.Count -ne 1 -or
        $calculated.responseCode -cne $fixtureObservation[0].expected.response) {
        throw "O09012 input for $expectedId does not reproduce its published response."
    }
}

foreach ($candidatePath in @($runnerPath, $calculatorPath)) {
    $codeWithoutComments = ([regex]::Replace(
        [System.IO.File]::ReadAllText($candidatePath), '\([^)]*\)', '',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant))
    if ($codeWithoutComments -match '(?im)(^|\s)(G0?[0-3]|M0?[3-6]|M0?[89]|T[0-9]+|H[0-9]+)(?=\s|$)') {
        throw "No-motion candidate contains a forbidden motion, spindle, coolant, or tool command: $candidatePath"
    }
}

Write-Host 'Haas verification grader tests passed: vector and identity PASS, FAIL, and NOT_RUN scenarios.'
