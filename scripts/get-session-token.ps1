# Obtains a TOLIK session token the same way the desktop client does: ClientTokenSource
# signs a one-time challenge with the DPAPI-sealed P-256 device key, Core verifies the
# signature and issues a session. No dev flag, no bootstrap code, works after Genesis.
#
# Replaces the old `TOLIK.Cli bootstrap` step in start-tolik.ps1. The CLI is retired; the
# script reuses the identity the desktop client already created, so there is exactly one
# device identity per installation instead of two.
#
# This script intentionally contains NO cryptography. Device signing lives in exactly one
# place — tools/SessionToken, which drives the same ClientTokenSource the client uses. The
# earlier PowerShell copy of that exchange drifted and broke twice (binary DPAPI blob read
# as base64; an ECDsa method absent from Windows PowerShell 5.1), so the duplicate is gone.
#
# Usage:
#   powershell -File scripts\get-session-token.ps1 [-CoreUrl http://127.0.0.1:5080] [-Quiet]
#
# Exit codes: 0 = token written to ~/.tolik/session-token, 1 = failure.

param(
    [string]$CoreUrl = "http://127.0.0.1:5080",
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$tokenPath = Join-Path $HOME ".tolik\session-token"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "tools\SessionToken\SessionToken.csproj"

if (-not (Test-Path $projectPath)) {
    Write-Host "ERROR: $projectPath not found - run this script from the repository." -ForegroundColor Red
    exit 1
}

# The tool reads CoreLinkOptions from the environment, so the URL is passed that way and
# the caller's own TOLIK_CORE_URL (if any) is respected.
if ($CoreUrl) { $env:TOLIK_CORE_URL = $CoreUrl }

$arguments = @('run', '--project', $projectPath, '--', '--out', $tokenPath)
if ($Quiet) { $arguments += '--quiet' }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: session renewal failed (see the message above)." -ForegroundColor Red
    exit 1
}

exit 0
