param(
    [string]$InputRoot = "",
    [string]$OutputRoot = "",
    [string]$WhisperModel = "base"
)
$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($InputRoot)) { $InputRoot = Join-Path $scriptRoot "..\voice_reference" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $scriptRoot "..\artifacts\cosyvoice-dataset" }

$scriptRoot = [IO.Path]::GetFullPath($scriptRoot)
$InputRoot = [IO.Path]::GetFullPath($InputRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$WavRoot = Join-Path $OutputRoot "wav"
if (!(Test-Path $WavRoot)) { New-Item -ItemType Directory -Force -Path $WavRoot | Out-Null }

$manifestPath = Join-Path $OutputRoot "manifest.csv"
if (Test-Path $manifestPath) {
    $existing = Import-Csv $manifestPath
    $existing = $existing | Where-Object { $_.status -ne "transcribed" }
    Write-Output "Found $($existing.Count) untranscribed files."
} else {
    $existing = $null
}

import-module -Force (Join-Path $scriptRoot "..\scripts\prepare-cosyvoice-dataset.ps1") -ArgumentList $InputRoot, $OutputRoot

$manifestCsv = Join-Path $OutputRoot "manifest.csv"
$manifestJson = Join-Path $OutputRoot "manifest.json"
$rows = Import-Csv $manifestCsv
$needsTranscription = $rows | Where-Object { $_.status -eq "needs_transcript" }

Write-Output "Transcribing $($needsTranscription.Count) files with Whisper ($WhisperModel)..."
$completed = 0
foreach ($row in $needsTranscription) {
    $wavFile = Join-Path $OutputRoot $row.wav
    if (!(Test-Path $wavFile)) { continue }
    Write-Output "[$($completed+1)/$($needsTranscription.Count)] Transcribing: $($row.wav)"
    
    $result = python -m whisper $wavFile --model $WhisperModel --language ru --fp16 false --output_format json --output_dir (Split-Path $manifestJson) 2>&1
    $jsonFile = Join-Path (Split-Path $manifestJson) ([IO.Path]::GetFileNameWithoutExtension($row.wav)) + ".json"
    
    if (Test-Path $jsonFile) {
        $transcription = Get-Content $jsonFile -Raw | ConvertFrom-Json
        $text = $transcription.text -replace '[^\w\sа-яА-ЯёЁ\-.,!?;:()\/\[\]{}"]', '' -replace '\s+', ' ' -trim
        if ([string]::IsNullOrWhiteSpace($text)) { $text = "[unintelligible]" }
        $row.text = $text
        $row.status = "transcribed"
        Write-Output "  -> $text"
        $completed++
    } else {
        $row.status = "transcription_failed"
        Write-Output "  -> FAILED"
    }
}

$rows | ConvertTo-Csv -NoTypeInformation | Set-Content -Encoding utf8 $manifestCsv
$rows | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 $manifestJson
Write-Output "Transcription complete. Completed: $completed files."
