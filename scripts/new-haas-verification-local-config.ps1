[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MachineId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z0-9-]{1,40}$')]
    [string] $MachineLabel,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [ValidateRange(9000, 9999)] [int] $ChallengeProgramNumber = 9001,
    [ValidateRange(9000, 9999)] [int] $VerifyProgramNumber = 9002,
    [ValidateRange(9000, 9999)] [int] $FinalizeProgramNumber = 9003,
    [ValidateRange(10000, 10999)] [int] $NonceVariable = 10501,
    [ValidateRange(1, 10999)] [int] $ResponseVariable = 10500,
    [ValidateRange(10000, 10999)] [int] $VerificationStateVariable = 10502,
    [ValidateRange(10000, 10999)] [int] $ReleaseTokenVariable = 10503,
    [ValidateRange(10000, 10999)] [int] $EventSequenceVariable = 10504,
    [ValidateRange(1, 999999)] [int] $MacroVersion = 10,
    [ValidateRange(4, 6)] [int] $ResponseDigits = 6,
    [ValidateRange(30, 3600)] [int] $VerificationTimeoutSeconds = 120,
    [Parameter(Mandatory = $true)]
    [ValidateRange(100000, 999999)] [int] $SampleNcIdentity,
    [Parameter(Mandatory = $true)]
    [ValidateRange(100000, 999999)] [int] $SampleOffsetReleaseToken,
    [ValidateRange(1, 8999)] [int] $TestNcProgramNumber = 1990,
    [ValidateRange(1, 8999)] [int] $TestOffsetLoaderProgramNumber = 1991,
    [ValidateRange(1, 8999)] [int] $TestCycleProgramNumber = 1992,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($MachineId)) { throw 'MachineId is required.' }
if ((@($ChallengeProgramNumber, $VerifyProgramNumber, $FinalizeProgramNumber) |
        Select-Object -Unique).Count -ne 3) {
    throw 'The three protected program numbers must be distinct.'
}
if ((@($TestNcProgramNumber, $TestOffsetLoaderProgramNumber, $TestCycleProgramNumber) |
        Select-Object -Unique).Count -ne 3) { throw 'Test program numbers must be distinct.' }
$canonicalResponseVariable = if ($ResponseVariable -ge 500 -and $ResponseVariable -le 549) {
    $ResponseVariable + 10000
} else { $ResponseVariable }
$variables = @($NonceVariable, $canonicalResponseVariable, $VerificationStateVariable,
    $ReleaseTokenVariable, $EventSequenceVariable)
if (($variables | Select-Object -Unique).Count -ne 5) {
    throw 'The five macro variables must be distinct after Haas legacy aliases are normalized.'
}
if (-not (($ResponseVariable -ge 500 -and $ResponseVariable -le 549) -or
          ($ResponseVariable -ge 10500 -and $ResponseVariable -le 10549))) {
    throw 'ResponseVariable must be in the Haas M109 range 500-549 or 10500-10549.'
}
if ($OutputPath -notmatch '\.local\.json$') {
    throw 'OutputPath must end in .local.json so repository ignore rules protect it.'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw "Refusing to overwrite '$resolvedOutput'. Use -Force after reviewing the target."
}
$config = [ordered]@{
        machineId = $MachineId.Trim()
        machineLabel = $MachineLabel
        challengeProgramNumber = $ChallengeProgramNumber
        verifyProgramNumber = $VerifyProgramNumber
        finalizeProgramNumber = $FinalizeProgramNumber
        nonceVariable = $NonceVariable
        responseVariable = $ResponseVariable
        verificationStateVariable = $VerificationStateVariable
        releaseTokenVariable = $ReleaseTokenVariable
        eventSequenceVariable = $EventSequenceVariable
        macroVersion = $MacroVersion
        responseDigits = $ResponseDigits
        verificationTimeoutSeconds = $VerificationTimeoutSeconds
        sampleNcIdentity = $SampleNcIdentity
        sampleOffsetReleaseToken = $SampleOffsetReleaseToken
        testNcProgramNumber = $TestNcProgramNumber
        testOffsetLoaderProgramNumber = $TestOffsetLoaderProgramNumber
        testCycleProgramNumber = $TestCycleProgramNumber
}
$parent = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrEmpty($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
[IO.File]::WriteAllText($resolvedOutput,
    (($config | ConvertTo-Json -Depth 3) + "`r`n"), [Text.Encoding]::ASCII)

Write-Host "Wrote local commissioning configuration: $resolvedOutput"
Write-Host 'No Machine Secret, derived key, token, or CNC identity credential is used.'
