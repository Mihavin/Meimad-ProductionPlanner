[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$grader = Join-Path $PSScriptRoot 'haas-verification-grade.ps1'
$fixtures = Join-Path $repositoryRoot 'tests\fixtures'

function Read-Grade([string]$fileName) {
    $path = Join-Path $fixtures $fileName
    return ((& $grader -InputPath $path) | ConvertFrom-Json)
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

Write-Host 'Haas verification grader tests passed: PASS, FAIL, and NOT_RUN scenarios.'
