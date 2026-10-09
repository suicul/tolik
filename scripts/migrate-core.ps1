# TOLIK Core Migration Protocol (docs/identity-presence.md §3, IP-2).
# Transactional migration: PREPARE -> VERIFY DESTINATION -> PAUSE (optional)
# -> SNAPSHOT -> SYNC -> INTEGRITY -> BOOT -> HEALTH -> EPOCH ACQUIRE+FENCE
# -> VERIFY -> FINALIZE. Abort-on-fail at every step.
# The lease fencing happens via the NEW core's own CoreLeaseService at startup
# (TryAcquireAsync fences expired/absent leases, refuses CONFLICT); the OLD core
# becomes STALE via its renewal loop and stops acting on its own. The script
# NEVER kills processes and NEVER writes to the DB directly.
# -WhatIfMode prints the plan without executing anything.
param(
    [string]$SourceBaseUrl = "http://127.0.0.1:5080",
    [string]$SourceToken = "",
    [string]$DestinationComposeDir = ".",
    [string]$DestinationProfile = "vps",
    [string]$DestinationPostgresPassword = "",
    [switch]$SkipSourcePause,
    [switch]$WhatIfMode
)
$ErrorActionPreference = "Stop"
$script:CurrentStep = ""

function Step([string]$name, [scriptblock]$body) {
    $script:CurrentStep = $name
    Write-Host "== [$name] ==" -ForegroundColor Cyan
    if ($WhatIfMode) { Write-Host "   (WhatIfMode: шаг не выполняется)"; return }
    & $body
    if ($LASTEXITCODE -gt 0) { Write-Error "ABORT at step [$name] (exit $LASTEXITCODE)"; exit 1 }
}

function Fail([string]$reason) {
    Write-Host "ABORT at step [$script:CurrentStep]: $reason" -ForegroundColor Red
    exit 1
}

# 1. PREPARE: source reachable + coreLease present; capture epoch/host.
Step "PREPARE" {
    try {
        $h = (Invoke-WebRequest -Uri "$SourceBaseUrl/health" -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
    } catch { Fail "source unreachable: $($_.Exception.Message)" }
    if (-not $h.coreLease) { Fail "source /health has no coreLease (old core image?)" }
    $script:SourceEpoch = $h.coreLease.epoch
    $script:SourceHost = $h.coreLease.host
    Write-Host "   source: host=$($h.coreLease.host) epoch=$($h.coreLease.epoch) stale=$($h.coreLease.stale) core=$($h.core) voice=$($h.voice) memory=$($h.memory)"
}

# 2. VERIFY DESTINATION: compose dir, docker, postgres password.
Step "VERIFY DESTINATION" {
    if (-not (Test-Path "$DestinationComposeDir/docker-compose.yml")) { Fail "docker-compose.yml not found in $DestinationComposeDir" }
    docker info *> $null; if ($LASTEXITCODE -ne 0) { Fail "docker daemon not reachable" }
    if (-not $DestinationPostgresPassword) { Fail "DestinationPostgresPassword is required" }
    Write-Host "   destination ok: $DestinationComposeDir (profile $DestinationProfile)"
}

# 3. PAUSE SOURCE (optional): no pause-all endpoint exists — honest warning.
Step "PAUSE SOURCE" {
    if ($SkipSourcePause) { Write-Host "   skipped (-SkipSourcePause)"; return }
    Write-Host "   ВНИМАНИЕ: pause-all endpoint отсутствует. Приостанови активные задачи через /tasks API вручную (опционально)."
}

# 4. SNAPSHOT SOURCE: existing snapshot discipline (code+docs+dump+manifest).
Step "SNAPSHOT SOURCE" {
    if ($WhatIfMode) { return }
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\snapshot-tolik.ps1
    if ($LASTEXITCODE -ne 0) { Fail "snapshot failed" }
}

# 5. SYNC DATA: snapshot bundle -> destination host.
Step "SYNC DATA" {
    $latest = Get-ChildItem "snapshots" -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { Fail "no snapshot directory found (step SNAPSHOT)" }
    $script:SnapshotPath = $latest.FullName
    Write-Host "   snapshot: $($latest.Name) (remote destination: передай скопом — scp/robocopy, параметризируется при реальной миграции)"
}

# 6. VERIFY INTEGRITY: SHA256 dump vs sidecar.
Step "VERIFY INTEGRITY" {
    $dump = Get-ChildItem $SnapshotPath -Filter "tolik-backup-*.dump" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dump) { Fail "no DB dump inside the snapshot" }
    if (-not (Test-Path "$($dump.FullName).sha256")) { Fail "no SHA256 sidecar for $($dump.Name)" }
    $expected = (Get-Content "$($dump.FullName).sha256" -Raw).Trim()
    $actual = (Get-FileHash $dump.FullName -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected.ToLower()) { Fail "dump hash mismatch: $actual != $expected" }
    Write-Host "   dump integrity ok: $($dump.Name)"
}

# 7. BOOT DESTINATION: docker compose build+up; DEV_AUTO_TOKEN must be off.
Step "BOOT DESTINATION" {
    if ($env:TOLIK_DEV_AUTO_TOKEN) { Write-Host "   ВНИМАНИЕ: TOLIK_DEV_AUTO_TOKEN установлен — destination НЕ должен auto-mint owner sessions!" -ForegroundColor Yellow }
    docker compose -f "$DestinationComposeDir/docker-compose.yml" --profile $DestinationProfile build 2>&1 | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Fail "destination build failed" }
    docker compose -f "$DestinationComposeDir/docker-compose.yml" --profile $DestinationProfile up -d 2>&1 | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Fail "destination up failed" }
}

# 8. HEALTH CHECK DESTINATION: poll until healthy (bounded).
Step "HEALTH CHECK DESTINATION" {
    # Destination base URL: same host for now (the compose publishes :5080);
    # при реальной миграции — параметризируется (DestinationBaseUrl).
    $destUrl = $SourceBaseUrl
    $healthy = $false
    foreach ($i in 1..24) {
        Start-Sleep -Seconds 5
        try {
            $h = (Invoke-WebRequest -Uri "$destUrl/health" -UseBasicParsing -TimeoutSec 5).Content | ConvertFrom-Json
            if ($h.core) { $healthy = $true; break }
        } catch { }
    }
    if (-not $healthy) { Fail "destination health timeout (120s)" }
    Write-Host "   destination healthy"
}

# 9. EPOCH ACQUIRE + FENCE OLD: the destination core's startup fences
#    expired/absent leases; CONFLICT (source still ACTIVE) -> honest abort.
Step "EPOCH ACQUIRE + FENCE OLD" {
    $destUrl = $SourceBaseUrl
    foreach ($i in 1..24) {
        Start-Sleep -Seconds 5
        try {
            $h = (Invoke-WebRequest -Uri "$destUrl/health" -UseBasicParsing -TimeoutSec 5).Content | ConvertFrom-Json
            if (-not $h.coreLease) { continue }
            if ($h.coreLease.epoch -gt $SourceEpoch -and -not $h.coreLease.stale) {
                Write-Host "   destination ACTIVE: epoch=$($h.coreLease.epoch) host=$($h.coreLease.host) (source epoch was $SourceEpoch)"
                $script:NewEpoch = $h.coreLease.epoch
                return
            }
            if ($h.coreLease.stale) { continue } # a stale destination runtime re-acquires
        } catch { }
    }
    Fail "source lease still ACTIVE — останови старый core первым (он станет STALE через renewal loop); скрипт не фенсит принудительно"
}

# 10. VERIFY: destination full component check + epoch comparison.
Step "VERIFY" {
    $destUrl = $SourceBaseUrl
    $h = (Invoke-WebRequest -Uri "$destUrl/health" -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
    if (-not ($h.core -and $h.memory)) { Fail "destination components unhealthy: core=$($h.core) memory=$($h.memory)" }
    if ($h.coreLease.epoch -le $SourceEpoch) { Fail "epoch did not advance: $($h.coreLease.epoch) <= $SourceEpoch" }
    Write-Host "   verify ok: core=$($h.core) memory=$($h.memory) lease epoch=$($h.coreLease.epoch)"
}

# 11. FINALIZE: summary + the old-core reminder.
Step "FINALIZE" {
    Write-Host "" 
    Write-Host "МИГРАЦИЯ ЗАВЕРШЕНА" -ForegroundColor Green
    Write-Host "  Identity: TOLIK (одна, непрерывна)"
    Write-Host "  Old core: host=$SourceHost epoch=$SourceEpoch -> STALE (renewal loop остановит действия сам)"
    Write-Host "  New core: epoch=$NewEpoch host=$($h.coreLease.host)"
    Write-Host "  Snapshot: $SnapshotPath"
    Write-Host "  Напоминание: останови процесс старого core (он уже не имеет права действовать — fenced)."
}
