param(
    [Parameter(Mandatory = $true)][string]$CredentialFile,
    [string]$ServerUri = 'http://127.0.0.1:5080'
)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$ServerUri
if (-not $uri.IsLoopback -or $uri.Scheme -notin @('http', 'https') -or $uri.UserInfo) {
    throw 'Run the recovery task on the Planner Server and use its loopback HTTP(S) address.'
}
# Export-Clixml must be created on this PC by the Windows account that runs this task.
$credential = Import-Clixml -LiteralPath $CredentialFile
if ($credential -isnot [PSCredential]) { throw 'CredentialFile must contain a Windows-protected PSCredential.' }
$session = $null
try {
    $body = @{ userName = $credential.UserName; password = $credential.GetNetworkCredential().Password; clientId = 'recovery-scheduled-task' } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$($ServerUri.TrimEnd('/'))/api/v1/auth/sign-in" -ContentType 'application/json' -Body $body
    $body = $null
    $headers = @{ Authorization = "Bearer $($session.token)" }
    $result = Invoke-RestMethod -Method Post -Uri "$($ServerUri.TrimEnd('/'))/api/v1/server-maintenance/recovery-sets" -Headers $headers -TimeoutSec 86400
    $result | ConvertTo-Json
} finally {
    $body = $null
    if ($session) {
        try { Invoke-RestMethod -Method Post -Uri "$($ServerUri.TrimEnd('/'))/api/v1/auth/sign-out" -Headers @{ Authorization = "Bearer $($session.token)" } | Out-Null }
        catch { Write-Warning 'The recovery task could not sign out; the session will expire normally.' }
    }
}
