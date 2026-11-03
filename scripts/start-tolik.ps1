# TOLIK one-command launcher (verified path, see docs/LIVE-DEMO.md).
# Starts: PostgreSQL + Voice + Core (Docker) ->
# device bootstrap (once) -> Windows Agent (new window) ->
# OpenCode server (new window, remote attach) -> browser UI.
# Ollama is NOT started or warmed (emergency route only); XTTS is opt-in (-WithXtts).
#
# Usage:
#   .\scripts\start-tolik.ps1                # everything
#   .\scripts\start-tolik.ps1 -NoAgent       # stack only, no agent window
#   .\scripts\start-tolik.ps1 -NoBrowser     # don't open the UI page
#   .\scripts\start-tolik.ps1 -NoOpenCode    # skip the OpenCode server window
param(
    [switch]$NoAgent,
    [switch]$NoBrowser,
    [switch]$WithXtts,
    [switch]$NoOpenCode,
    [int]$HeartbeatSec = 2
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$CoreUrl = "http://127.0.0.1:5080"
$Model = "qwen2.5:3b-instruct"
$tokenPath = Join-Path $HOME ".tolik\session-token"

Write-Host "== TOLIK launcher ==" -ForegroundColor Cyan

# 0. Prerequisites: docker. Ollama is optional — it is the EMERGENCY route only
# (no internet), never started or warmed by default.
if (-not (Get-Command "docker" -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: 'docker' CLI not found in PATH." -ForegroundColor Red
    exit 1
}
$hasOllama = [bool](Get-Command "ollama" -ErrorAction SilentlyContinue)

# 1. Emergency local model: pulled ONLY on request, never at startup (VRAM stays free).
if ($hasOllama) {
    Write-Host "Ollama present (emergency route only, not started, not warmed)." -ForegroundColor DarkGray
} else {
    Write-Host "Ollama absent: cloud-only operation (no emergency fallback)." -ForegroundColor DarkGray
}

# 2. Stack (secrets stay in .env; never printed here).
# Dev launcher opt-in: the same-machine page may mint its own owner session,
# so no hand-copied tokens are needed. Never set this for shared/VPS hosts.
$env:TOLIK_DEV_AUTO_TOKEN = 'true'
# Neural voice sidecar (GPU, Ordis voice). OPT-IN via -WithXtts: by default TTS is
# edge-tts (cloud, no VRAM) per the owner decision of 02.11.2026.
$profiles = @('--profile', 'vps')
if ($WithXtts) {
    try {
        docker run --rm --gpus all nvidia/cuda:12.6.0-base-ubuntu22.04 nvidia-smi --query-gpu=name --format=csv,noheader 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $profiles += @('--profile', 'gpu') }
        else { Write-Host "No NVIDIA runtime: starting without XTTS voice." -ForegroundColor DarkGray }
    } catch { Write-Host "No NVIDIA runtime: starting without XTTS voice." -ForegroundColor DarkGray }
}
Write-Host "Starting stack (postgres + voice + core$(if ($WithXtts) { ' + xtts' }))..." -ForegroundColor Yellow
docker compose $profiles up -d | Out-Null

# 3. Wait for Core health (proxy-free HttpClient: Win PowerShell 5.1 would otherwise
# stall on proxy autodetect for localhost and false-negative the whole launch)
Add-Type -AssemblyName System.Net.Http
$httpHandler = New-Object System.Net.Http.HttpClientHandler
$httpHandler.UseProxy = $false
$http = New-Object System.Net.Http.HttpClient($httpHandler)
$http.Timeout = [TimeSpan]::FromSeconds(5)
try {
    $deadline = (Get-Date).AddSeconds(90)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $res = $http.GetAsync("$CoreUrl/health").GetAwaiter().GetResult()
            if ($res.StatusCode -eq 200) { $healthy = $true; break }
        } catch { Start-Sleep -Seconds 3 }
    }
    if (-not $healthy) {
        Write-Host "ERROR: Core did not become healthy at $CoreUrl/health" -ForegroundColor Red
        exit 1
    }
} finally {
    $http.Dispose()
    $httpHandler.Dispose()
}
Write-Host "Core healthy." -ForegroundColor Green

# 4. Session token for the device (renewed by signature; the device key is reused).
# Replaces the retired TOLIK.Cli bootstrap step: the script signs a one-time challenge
# with the same DPAPI-sealed device key the TOLIK client uses.
if (-not (Test-Path $tokenPath)) {
    Write-Host "No session token: renewing by device signature..." -ForegroundColor Yellow
    & powershell -NoProfile -File (Join-Path $PSScriptRoot 'get-session-token.ps1') -CoreUrl $CoreUrl
    if (-not (Test-Path $tokenPath)) {
        Write-Host "ERROR: session renewal failed, no token at $tokenPath" -ForegroundColor Red
        Write-Host "Register the device in the TOLIK client (code from TOLIK_BOOTSTRAP_CODE) first." -ForegroundColor Yellow
        exit 1
    }
}
Write-Host "Session token present." -ForegroundColor Green

# 5. Windows Agent in its own window (dist exe preferred, dotnet run fallback).
# Token flows via TOLIK_SESSION_TOKEN env (audit m6): command lines are readable
# by any local process via Win32_Process (threats T3/T8) — never the CLI arg.
if (-not $NoAgent) {
    $token = (Get-Content $tokenPath -Raw).Trim()
    $agentExe = Join-Path $root 'dist\agent\TOLIK.Agent.exe'
    if (Test-Path $agentExe) {
        $agentCmd = "`$env:TOLIK_ALLOWED_ROOTS='$root'; `$env:TOLIK_SESSION_TOKEN='$token'; & '$agentExe' --core-url $CoreUrl --heartbeat-sec $HeartbeatSec"
    } else {
        $agentCmd = "`$env:TOLIK_ALLOWED_ROOTS='$root'; `$env:TOLIK_SESSION_TOKEN='$token'; dotnet run --project src\TOLIK.Agent -- --core-url $CoreUrl --heartbeat-sec $HeartbeatSec"
    }
    Write-Host "Starting Windows Agent..." -ForegroundColor Yellow
    Start-Process -FilePath "powershell" -ArgumentList @("-NoExit", "-Command", $agentCmd) -WorkingDirectory $root -WindowStyle Normal | Out-Null
} else {
    Write-Host "Skipping agent (-NoAgent)." -ForegroundColor DarkGray
}

# 6. OpenCode server in its own window (remote attach from anywhere on the tailnet).
# Sessions live server-side, so `opencode attach` from the Linux laptop continues
# the same chat. Fixed port (serve defaults to random). Skipped when opencode
# is not installed; tailnet exposure itself is a separate `tailscale serve` step
# (see docs/REMOTE-ACCESS.md) — never exposed to the internet.
$OpenCodePort = 4096
if (-not $NoOpenCode) {
    if (Get-Command opencode -ErrorAction SilentlyContinue) {
        Write-Host "Starting OpenCode server (attach: opencode attach http://127.0.0.1:$OpenCodePort)..." -ForegroundColor Yellow
        $ocCmd = "opencode serve --port $OpenCodePort --hostname 127.0.0.1"
        Start-Process -FilePath "powershell" -ArgumentList @("-NoExit", "-Command", $ocCmd) -WorkingDirectory $root -WindowStyle Normal | Out-Null
    } else {
        Write-Host "opencode CLI not found, skipping server (-NoOpenCode implied)." -ForegroundColor DarkGray
    }
} else {
    Write-Host "Skipping OpenCode server (-NoOpenCode)." -ForegroundColor DarkGray
}

# 7. UI
if (-not $NoBrowser) {
    Write-Host "Opening Cephalon UI..." -ForegroundColor Yellow
    Start-Process "$CoreUrl/"
    Write-Host "Paste the token from $tokenPath into the Bearer token field." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "TOLIK is live: $CoreUrl/  (health: $CoreUrl/health)" -ForegroundColor Cyan
Write-Host "First phrase to try: 'открой калькулятор'" -ForegroundColor Cyan
