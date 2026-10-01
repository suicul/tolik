# TOLIK one-command launcher (verified path, see docs/LIVE-DEMO.md).
# Starts: PostgreSQL + Voice + Core (Docker) -> Ollama model check ->
# device bootstrap (once) -> Windows Agent (new window) ->
# OpenCode server (new window, remote attach) -> browser UI.
#
# Usage:
#   .\scripts\start-tolik.ps1                # everything
#   .\scripts\start-tolik.ps1 -NoAgent       # stack only, no agent window
#   .\scripts\start-tolik.ps1 -NoBrowser     # don't open the UI page
#   .\scripts\start-tolik.ps1 -NoOpenCode    # skip the OpenCode server window
param(
    [switch]$NoAgent,
    [switch]$NoBrowser,
    [switch]$NoXtts,
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

# 0. Prerequisites: docker + ollama CLIs
foreach ($cmd in @("docker", "ollama")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Host "ERROR: '$cmd' CLI not found in PATH." -ForegroundColor Red
        exit 1
    }
}

# 1. Model on the host (Ollama keeps it warm itself via keep_alive=60m per request)
$models = (ollama list 2>$null) -join "`n"
if ($models -notmatch [regex]::Escape("qwen2.5:3b")) {
    Write-Host "Pulling model $Model (once)..." -ForegroundColor Yellow
    ollama pull $Model
} else {
    Write-Host "Model $Model present." -ForegroundColor Green
}

# 2. Stack (secrets stay in .env; never printed here).
# Dev launcher opt-in: the same-machine page may mint its own owner session,
# so no hand-copied tokens are needed. Never set this for shared/VPS hosts.
$env:TOLIK_DEV_AUTO_TOKEN = 'true'
# Neural voice sidecar (GPU, Ordis voice). Skipped with -NoXtts or when the
# NVIDIA runtime is absent — the voice service falls back to edge/offline.
$profiles = @('--profile', 'vps')
if (-not $NoXtts) {
    try {
        docker run --rm --gpus all nvidia/cuda:12.6.0-base-ubuntu22.04 nvidia-smi --query-gpu=name --format=csv,noheader 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $profiles += @('--profile', 'gpu') }
        else { Write-Host "No NVIDIA runtime: starting without XTTS voice." -ForegroundColor DarkGray }
    } catch { Write-Host "No NVIDIA runtime: starting without XTTS voice." -ForegroundColor DarkGray }
}
Write-Host "Starting stack (postgres + voice + core$(if (-not $NoXtts) { ' + xtts' }))..." -ForegroundColor Yellow
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

# 4. Device bootstrap (once; re-run is safe — the device key is reused).
# Prefers the self-contained dist CLI (no .NET SDK required), falls back to dotnet run.
$cliExe = Join-Path $root 'dist\cli\TOLIK.Cli.exe'
if (-not (Test-Path $tokenPath)) {
    Write-Host "First run: enrolling this device..." -ForegroundColor Yellow
    if (Test-Path $cliExe) { & $cliExe bootstrap | Out-Null }
    else { dotnet run --project clients\TOLIK.Cli -- bootstrap | Out-Null }
    if (-not (Test-Path $tokenPath)) {
        Write-Host "ERROR: bootstrap failed, no token at $tokenPath" -ForegroundColor Red
        exit 1
    }
}
Write-Host "Session token present." -ForegroundColor Green

# 5. Windows Agent in its own window (dist exe preferred, dotnet run fallback)
if (-not $NoAgent) {
    $token = (Get-Content $tokenPath -Raw).Trim()
    $agentExe = Join-Path $root 'dist\agent\TOLIK.Agent.exe'
    if (Test-Path $agentExe) {
        $agentCmd = "`$env:TOLIK_ALLOWED_ROOTS='$root'; & '$agentExe' --core-url $CoreUrl --token '$token' --heartbeat-sec $HeartbeatSec"
    } else {
        $agentCmd = "`$env:TOLIK_ALLOWED_ROOTS='$root'; dotnet run --project src\TOLIK.Agent -- --core-url $CoreUrl --token '$token' --heartbeat-sec $HeartbeatSec"
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
