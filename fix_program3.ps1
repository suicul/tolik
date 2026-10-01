$content = [IO.File]::ReadAllText('C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs', [Text.Encoding]::UTF8)

# Fix the broken $"" strings from the bad replacement
$content = $content -replace '\$\$\"', '\$\"'
$content = $content -replace '\$\$\"', '\$\"'
$content = $content -replace '\$\$\"', '\$\"'

[IO.File]::WriteAllText('C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs', $content, [Text.Encoding]::UTF8)
Write-Host "Fixed"