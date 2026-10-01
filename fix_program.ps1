$content = Get-Content 'C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs' -Raw
$content = $content -replace 'return Results.Accepted\(new', 'return Results.Accepted($"/tasks/{taskId}/confirmations/{ex.Confirmation.Id}", new'
Set-Content 'C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs' -Value $content -Encoding UTF8
Write-Host 'Done'