[CmdletBinding()]
param(
    [string]$Endpoint = 'http://127.0.0.1:5099',
    [string]$BootstrapCode = 'challenge-test-code'
)

$ErrorActionPreference = 'Stop'
$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    $der = $key.ExportSubjectPublicKeyInfo()
    $publicKey = "-----BEGIN PUBLIC KEY-----`n" + [Convert]::ToBase64String($der, 'InsertLineBreaks') + "`n-----END PUBLIC KEY-----"
    $challengeResponse = Invoke-RestMethod -Method Post -Uri "$Endpoint/auth/bootstrap/challenge" -ContentType 'application/json' -Body (@{ bootstrapCode = $BootstrapCode } | ConvertTo-Json)
    $challenge = $challengeResponse.challenge
    $signature = [Convert]::ToBase64String($key.SignData([Text.Encoding]::UTF8.GetBytes($challenge), [Security.Cryptography.HashAlgorithmName]::SHA256))
    $enrollment = Invoke-RestMethod -Method Post -Uri "$Endpoint/auth/bootstrap/enroll" -ContentType 'application/json' -Body (@{
        bootstrapCode = $BootstrapCode
        installationId = "challenge-install-$([Guid]::NewGuid().ToString('N'))"
        publicKey = $publicKey
        challenge = $challenge
        signature = $signature
    } | ConvertTo-Json)
    if (-not $enrollment.sessionToken -or -not $enrollment.sessionId) { throw 'Enrollment response omitted session material.' }
    Write-Output "CHALLENGE_OK sessionId=$($enrollment.sessionId)"
}
finally {
    $key.Dispose()
}
