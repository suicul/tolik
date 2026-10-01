[CmdletBinding()]
param(
    [string]$Endpoint = 'http://127.0.0.1:5102',
    [string]$BootstrapCode = 'task-code',
    [string]$ReadPath = 'C:/DEV/TOLIK/README.md'
)

$ErrorActionPreference = 'Stop'
$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    $publicKey = "-----BEGIN PUBLIC KEY-----`n" + [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo(), 'InsertLineBreaks') + "`n-----END PUBLIC KEY-----"
    $challengeResponse = Invoke-RestMethod -Method Post -Uri "$Endpoint/auth/bootstrap/challenge" -ContentType 'application/json' -Body (@{ bootstrapCode = $BootstrapCode } | ConvertTo-Json)
    $signature = [Convert]::ToBase64String($key.SignData([Text.Encoding]::UTF8.GetBytes($challengeResponse.challenge), [Security.Cryptography.HashAlgorithmName]::SHA256))
    $enrollment = Invoke-RestMethod -Method Post -Uri "$Endpoint/auth/bootstrap/enroll" -ContentType 'application/json' -Body (@{ bootstrapCode = $BootstrapCode; installationId = "task-install-$([Guid]::NewGuid().ToString('N'))"; publicKey = $publicKey; challenge = $challengeResponse.challenge; signature = $signature } | ConvertTo-Json)
    $headers = @{ Authorization = "Bearer $($enrollment.sessionToken)" }
    $task = Invoke-RestMethod -Method Post -Uri "$Endpoint/tasks" -Headers $headers -ContentType 'application/json' -Body (@{ description = "read_file:$ReadPath" } | ConvertTo-Json)
    $plan = Invoke-RestMethod -Method Post -Uri "$Endpoint/tasks/$($task.id)/plan" -Headers $headers -ContentType 'application/json' -Body (@{ request = "read_file:$ReadPath"; maxSteps = 1; budgetSeconds = 30 } | ConvertTo-Json)
    $execution = Invoke-RestMethod -Method Post -Uri "$Endpoint/tasks/$($task.id)/execute" -Headers $headers -ContentType 'application/json' -Body (@{ request = "read_file:$ReadPath"; maxSteps = 1; budgetSeconds = 30 } | ConvertTo-Json)
    if ($execution.results.Count -ne 1) { throw 'Expected exactly one execution result.' }
    Write-Output "TASK_OK taskId=$($task.id) planSteps=$($plan.steps.Count) resultLength=$($execution.results[0].Length)"
}
finally { $key.Dispose() }
