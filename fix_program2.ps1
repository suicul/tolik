$content = [IO.File]::ReadAllText('C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs', [Text.Encoding]::UTF8)

# Fix the broken interpolated strings from the bad replacement
$content = $content -replace '\\$\\"/tasks/\{taskId\}/confirmations/\{ex\.Confirmation\.Id\}\"', '\$"/tasks/{taskId}/confirmations/{ex.Confirmation.Id}"'
$content = $content -replace '\\$\"/tasks/\{taskId\}/confirmations/\{ex\.Confirmation\.Id\}"', '\$"/tasks/{taskId}/confirmations/{ex.Confirmation.Id}"'
$content = $content -replace '\\$\"/tasks/\{taskId\}/confirmations/\{ex\.Confirmation\.Id\}"', '\$"/tasks/{taskId}/confirmations/{ex.Confirmation.Id}"'

# Also fix any double-dollar issues
$content = $content -replace '\$\$', '\$'

[IO.File]::WriteAllText('C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs', $content, [Text.Encoding]::UTF8)
Write-Host "Fixed"