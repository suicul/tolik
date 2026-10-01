# TOLIK end-to-end MVP smoke test.
# Verifies: enrollment -> health -> chat ("открой калькулятор") -> memory -> agents.
# Requires: PostgreSQL up, Core running, CLI built.
# Usage: .\scripts\test-voice-e2e.ps1 [-Endpoint http://127.0.0.1:5080] [-SkipCalculator]
param(
    [string]$Endpoint = "http://127.0.0.1:5080",
    [string]$BootstrapCode = "TLK-BSTEP-2026-INIT",
    [switch]$SkipCalculator
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root "clients\TOLIK.Cli\bin\Debug\net10.0\TOLIK.Cli.exe"
if (-not (Test-Path $cli)) {
    Write-Host "CLI not built. Run: dotnet build clients\TOLIK.Cli\TOLIK.Cli.csproj" -ForegroundColor Red
    exit 1
}

$pass = 0; $fail = 0
function Check($name, $condition) {
    if ($condition) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  [FAIL] $name" -ForegroundColor Red; $script:fail++ }
}

Write-Host "== TOLIK E2E Smoke ==" -ForegroundColor Cyan

# 1. Health (no auth needed)
try {
    $health = Invoke-RestMethod -Uri "$Endpoint/health" -Method Get -TimeoutSec 5
    Check "health endpoint" ($health.status -eq "Healthy")
} catch { Check "health endpoint" $false }

# 2. Bootstrap enroll through the CLI
Write-Host "-- bootstrap enroll (interactive key stored in ~/.tolik) --"
& $cli bootstrap --code $BootstrapCode --endpoint $Endpoint
Check "bootstrap enroll" ($LASTEXITCODE -eq 0)
if ($LASTEXITCODE -ne 0) { Write-Host "Enrollment failed — aborting."; exit 1 }

$tokenFile = Join-Path $HOME ".tolik\session-token"
$token = (Get-Content $tokenFile -Raw).Trim()
Check "session token saved" ($token.Length -gt 0)

$common = @("--endpoint", $Endpoint, "--token", $token)

# 3. Authenticated status
$statusJson = & $cli status @common | ConvertFrom-Json
Check "authenticated /status" ($statusJson.data.status -eq "Healthy")

# 4. Memory write + search
$null = & $cli chat "запомни: тестовая память E2E 20260924" @common
$memoryJson = & $cli memory --query "тестовая память E2E" @common | ConvertFrom-Json
Check "memory search finds entry" ($null -ne $memoryJson.data.persisted)

# 5. Calculator (the MVP action)
if (-not $SkipCalculator) {
    $calcJson = & $cli chat "открой калькулятор" @common | ConvertFrom-Json
    Check "calculator opens via chat" ($calcJson.data.action -eq "run_process")
    Start-Sleep -Seconds 2
    $calcProc = Get-Process -Name "calc", "CalculatorApp", "ApplicationFrameHost" -ErrorAction SilentlyContinue
    Check "calculator process running" ($null -ne $calcProc)
}

# 6. Agents list
$agentsJson = & $cli agents @common | ConvertFrom-Json
Check "agents listable" ($null -ne $agentsJson.data)

Write-Host ""
Write-Host "Result: PASS=$pass FAIL=$fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
