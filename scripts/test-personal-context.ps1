[CmdletBinding()]
param([string]$Endpoint='http://127.0.0.1:5080',[string]$BootstrapCode='personal-context-code')
$ErrorActionPreference='Stop'
$key=[System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
  $pub="-----BEGIN PUBLIC KEY-----" + [Environment]::NewLine + [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo(), 'InsertLineBreaks') + [Environment]::NewLine + "-----END PUBLIC KEY-----"
  $ch=Invoke-RestMethod -Method Post -Uri "$Endpoint/auth/bootstrap/challenge" -ContentType 'application/json' -Body (@{bootstrapCode=$BootstrapCode}|ConvertTo-Json)
  $sig=[Convert]::ToBase64String($key.SignData([Text.Encoding]::UTF8.GetBytes($ch.challenge),[Security.Cryptography.HashAlgorithmName]::SHA256))
  $en=Invoke-RestMethod -Method Post -Uri "$Endpoint/auth/bootstrap/enroll" -ContentType 'application/json' -Body (@{bootstrapCode=$BootstrapCode;installationId="personal-$([guid]::NewGuid().ToString('N'))";publicKey=$pub;challenge=$ch.challenge;signature=$sig}|ConvertTo-Json)
  $h=@{Authorization="Bearer $($en.sessionToken)"}
  $p=Invoke-WebRequest -Method Post -Uri "$Endpoint/personality" -Headers $h -ContentType 'application/json' -Body (@{tone='calm';style='tactical';useHumor=$true}|ConvertTo-Json)
  $l=Invoke-WebRequest -Method Post -Uri "$Endpoint/family-lore" -Headers $h -ContentType 'application/json' -Body (@{content='QA family lore marker'}|ConvertTo-Json)
  $pg=Invoke-WebRequest -Uri "$Endpoint/personality" -Headers $h
  $lg=Invoke-WebRequest -Uri "$Endpoint/family-lore" -Headers $h
  if($p.StatusCode -ne 200 -or $l.StatusCode -ne 200 -or $pg.StatusCode -ne 200 -or $lg.StatusCode -ne 200){throw 'Personal context smoke failed.'}
  "PERSONAL_CONTEXT_OK personalityPost=$($p.StatusCode) lorePost=$($l.StatusCode) personalityGet=$($pg.StatusCode) loreGet=$($lg.StatusCode)"
} finally { $key.Dispose() }
