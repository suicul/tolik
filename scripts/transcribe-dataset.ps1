# TOLIK dataset transcription via the running voice service (services/voice/server.py).
# Produces JSONL used by CosyVoice fine-tune: { "wav": path, "text": transcript }
# Idempotent: skips files already present in the output manifest.
# Uses curl.exe for the multipart upload (Invoke-RestMethod -Form requires
# PowerShell 6+, unavailable under Windows PowerShell 5.1).
[CmdletBinding()]
param(
    [string]$SourceDir = "artifacts\cosyvoice-dataset\clips",
    [string]$OutDir = "datasets\transcripts",
    [string]$VoiceService = "http://127.0.0.1:9660",
    [int]$Limit = 0
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'curl.exe not found in PATH.' }
$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $root $SourceDir
New-Item -ItemType Directory -Force -Path (Join-Path $root $OutDir) | Out-Null
$manifest = Join-Path (Join-Path $root $OutDir) "manifest.jsonl"

$done = @{}
if (Test-Path $manifest) {
    Get-Content $manifest | ForEach-Object {
        try { $done[($_ | ConvertFrom-Json).wav] = $true } catch { }
    }
}

# Voice service must be running (docker compose --profile vps up voice).
try {
    $health = Invoke-RestMethod -Uri "$VoiceService/health" -Method Get -TimeoutSec 5
    Write-Host "Voice service: ok" -ForegroundColor Green
} catch {
    Write-Host "Voice service is not running at $VoiceService" -ForegroundColor Red
    Write-Host "Start it first: docker compose --profile vps up -d voice"
    exit 1
}

$files = Get-ChildItem -LiteralPath $sourcePath -Recurse -File | Where-Object { $_.Extension -eq '.wav' }
if ($Limit -gt 0) { $files = $files | Select-Object -First $Limit }
Write-Host "Found $($files.Count) clips, already transcribed: $($done.Count)" -ForegroundColor Cyan

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$count = 0
$skipped = 0
foreach ($file in $files) {
    if ($done.ContainsKey($file.FullName)) { continue }
    # curl writes UTF-8: go through a temp file read explicitly as UTF-8
    # (Windows PowerShell 5.1 decodes native stdout with the console codepage).
    $tmpJson = Join-Path ([IO.Path]::GetTempPath()) ('tolik-stt-{0}.json' -f [Guid]::NewGuid().ToString('N'))
    & curl.exe -s -S --max-time 120 -X POST -F "file=@$($file.FullName)" "$VoiceService/stt/transcribe" -o $tmpJson
    if ($LASTEXITCODE -ne 0) { Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue; $skipped++; continue }
    try {
        $result = Get-Content -LiteralPath $tmpJson -Encoding utf8 -Raw | ConvertFrom-Json
    } catch { Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue; $skipped++; continue }
    Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
    $text = if ($result.text) { $result.text.Trim() } else { '' }
    if ($text.Length -gt 0) {
        $entry = @{ wav = $file.FullName; text = $text; durationSeconds = $result.durationSeconds } | ConvertTo-Json -Compress
        Add-Content -Path $manifest -Value $entry -Encoding utf8
        $count++
    } else { $skipped++ }
    if (($count + $skipped) % 25 -eq 0 -and ($count + $skipped) -gt 0) {
        Write-Host "  done=$($count + $skipped)/$($files.Count) ok=$count skipped=$skipped elapsed=$([int]$sw.Elapsed.TotalMinutes)m" -ForegroundColor DarkGray
    }
}

Write-Host "Done. New transcripts: $count (skipped $skipped) -> $manifest" -ForegroundColor Green
