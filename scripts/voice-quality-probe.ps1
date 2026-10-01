# Voice clarity probe: synthesize fixed phrases via XTTS, transcribe via the voice
# service (ru), report word match rate. Objective proxy for intelligibility —
# no ears required. Used to compare prompts, dictionaries and DSP settings.
[CmdletBinding()]
param(
    [string]$Xtts = 'http://127.0.0.1:9670',
    [string]$Voice = 'http://127.0.0.1:9660',
    [string]$OutDir = 'artifacts/voice-quality'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$phrases = @(
    'Оператор, все системы в норме. Ордис завершил диагностику.'
    'Ордис зафиксировал странную активность возле Цетуса.'
    'Проверь моды, оператор. Возможно, найдётся что-то полезное для Варфрейма.'
    'Корабль получил пробоину. Кислород заканчивается.'
    'Лотос, ты слышишь меня? Ответь, Лотос.'
    'Навигация ждёт. Куда летим, оператор?'
)

$results = foreach ($i in 0..($phrases.Count - 1)) {
    $wav = Join-Path $outPath ("probe_{0:00}.wav" -f $i)
    if (-not (Test-Path -LiteralPath $wav)) {
        $body = @{ text = $phrases[$i]; language = 'ru' } | ConvertTo-Json
        Invoke-RestMethod -Uri "$Xtts/tts" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 120 -OutFile $wav
    }
    # curl writes UTF-8 bytes: read the file explicitly as UTF-8 (PowerShell 5.1
    # would otherwise decode native stdout with the console codepage -> mojibake).
    $sttJson = Join-Path $outPath ("probe_{0:00}.json" -f $i)
    & curl.exe -s -S --max-time 60 -X POST -F "file=@$wav" -F 'language=ru' -o $sttJson "$Voice/stt/transcribe"
    if ($LASTEXITCODE -ne 0) { throw "STT failed for probe $i" }
    $stt = Get-Content -LiteralPath $sttJson -Encoding utf8 -Raw | ConvertFrom-Json
    [pscustomobject]@{ id = $i; expected = $phrases[$i]; heard = $stt.text }
}

$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $outPath 'probe.json')
python "$PSScriptRoot/score_probe.py" (Join-Path $outPath 'probe.json')
