param(
    [Parameter(Mandatory = $true)][ValidateRange(100000, 999999)][int]$Nonce,
    [Parameter(Mandatory = $true)][ValidateRange(100000, 999999)][int]$OffsetReleaseToken,
    [Parameter(Mandatory = $true)][ValidateRange(100000, 999999)][int]$NcIdentityToken,
    [Parameter(Mandatory = $true)][ValidateRange(100000, 999999)][int]$TestMachineKey,
    [ValidateRange(4, 6)][int]$ResponseDigits = 6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Add-SixDigits {
    param([System.Collections.Generic.List[int]]$Symbols, [int]$Value)
    foreach ($character in $Value.ToString('D6', [Globalization.CultureInfo]::InvariantCulture).ToCharArray()) {
        $Symbols.Add([int][char]::GetNumericValue($character))
    }
}

$symbols = [System.Collections.Generic.List[int]]::new()
$symbols.Add(1)
Add-SixDigits $symbols $Nonce
Add-SixDigits $symbols $OffsetReleaseToken
Add-SixDigits $symbols $NcIdentityToken
Add-SixDigits $symbols $TestMachineKey
foreach ($digit in @(3, 1, 4, 1, 5, 9)) { $symbols.Add($digit) }

$state = 7919
foreach ($symbol in $symbols) {
    $state = ($state % 90909) * 11 + $symbol
}

$modulus = [int][Math]::Pow(10, $ResponseDigits)
$response = ($state % $modulus).ToString(
    "D$ResponseDigits", [Globalization.CultureInfo]::InvariantCulture)

[pscustomobject]@{
    algorithmVersion = 1
    nonce = $Nonce
    offsetReleaseToken = $OffsetReleaseToken
    ncIdentityToken = $NcIdentityToken
    responseDigits = $ResponseDigits
    responseCode = $response
    warning = 'TEST VECTORS ONLY - do not pass a production Machine key on a command line.'
} | ConvertTo-Json
