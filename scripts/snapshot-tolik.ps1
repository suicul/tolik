# TOLIK snapshot discipline (docs/backup-recovery.md gap #3: "No code/weights/docs
# snapshot discipline (only DB is covered)").
# Creates a versioned snapshot bundle: code+docs (git archive at HEAD), the latest
# validated DB dump (copied from backups/), optional weights (env-configured), and a
# manifest (commit hash + contents). Retention: keep last N snapshots.
# Snapshots are plaintext secrets (like DB dumps) - gitignored, never committed.
param(
    [string]$SnapshotDir = "snapshots",
    [int]$Keep = 5,
    [string]$WeightsDir = $env:TOLIK_SNAPSHOT_WEIGHTS_DIR
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path ".git")) { Write-Error "Run from the repository root (.git not found)."; exit 1 }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$snap = Join-Path $SnapshotDir "tolik-snapshot-$stamp"
New-Item -ItemType Directory -Path $snap -Force | Out-Null

# 1. Code + docs: git archive at HEAD (tracked files only - secrets never included).
$codeArchive = Join-Path $snap "code-docs.zip"
git archive --format=zip -o $codeArchive HEAD
if ($LASTEXITCODE -ne 0) { Write-Error "git archive failed."; exit 1 }

# 2. The latest validated DB dump (copy from backups/ with its SHA-256 sidecar).
$latestDump = Get-ChildItem "backups" -Filter "tolik-backup-*.dump" -ErrorAction SilentlyContinue |
    Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
if ($latestDump) {
    Copy-Item $latestDump.FullName $snap
    if (Test-Path "$($latestDump.FullName).sha256") { Copy-Item "$($latestDump.FullName).sha256" $snap }
    Write-Host "DB dump included: $($latestDump.Name)"
} else {
    Write-Host "No DB dump in backups/ - run backup-postgres.ps1 first (snapshot covers code+docs only)."
}

# 3. Weights (optional): consent-safe original-voice training material (never committed).
if ($WeightsDir -and (Test-Path $WeightsDir)) {
    $weightsTarget = Join-Path $snap "weights"
    Copy-Item $WeightsDir $weightsTarget -Recurse -Force
    Write-Host "Weights included: $WeightsDir"
} else {
    Write-Host "Weights not included (TOLIK_SNAPSHOT_WEIGHTS_DIR unset or missing)."
}

# 4. Manifest: commit hash, date, contents.
$commit = git rev-parse HEAD
@(
    "TOLIK snapshot $stamp",
    "commit: $commit",
    "created: $(Get-Date -Format o)",
    "contents:",
    (Get-ChildItem $snap -Recurse -File | ForEach-Object { "  $($_.FullName.Substring($snap.Length + 1)) ($($_.Length) B)" })
) | Out-File (Join-Path $snap "MANIFEST.txt") -Encoding utf8

# 5. Retention: keep last N snapshot folders.
$snapshots = Get-ChildItem $SnapshotDir -Directory -Filter "tolik-snapshot-*" |
    Sort-Object CreationTimeUtc -Descending
$old = $snapshots | Select-Object -Skip $Keep
foreach ($s in $old) { Remove-Item $s.FullName -Recurse -Force }
if ($old) { Write-Host "Retention: pruned $($old.Count) old snapshots (keep=$Keep)." }

Write-Host "Snapshot created: $snap (commit $commit)"
