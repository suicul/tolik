[CmdletBinding()]
param(
    [string]$InputRoot,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg not found in PATH.' }
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw 'ffprobe not found in PATH.' }
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($InputRoot)) { $InputRoot = Join-Path $scriptRoot '..\voice_reference' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $scriptRoot '..\artifacts\cosyvoice-dataset' }
$InputRoot = [IO.Path]::GetFullPath($InputRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$WavRoot = Join-Path $OutputRoot 'wav'
New-Item -ItemType Directory -Force -Path $WavRoot | Out-Null
$inputPrefix = $InputRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$outputPrefix = $OutputRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar

$rows = @(foreach ($source in Get-ChildItem -Path $InputRoot -Recurse -File |
    Where-Object { $_.Extension -match '^\.(mp3|wav|flac|m4a|ogg|opus)$' }) {
    $relative = $source.FullName.Substring($inputPrefix.Length)
    $relativeUnix = $relative.Replace('\','/')
    $stem = [IO.Path]::GetFileNameWithoutExtension($relative).ToLowerInvariant() -replace '[^a-z0-9]+','_'
    $utt = 'ref_' + $stem.Trim('_')
    $target = Join-Path $WavRoot ([IO.Path]::ChangeExtension($relative, '.wav'))
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    & ffmpeg -hide_banner -loglevel error -y -i $source.FullName -ac 1 -ar 24000 -sample_fmt s16 $target
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $($source.FullName)" }
    $seconds = (& ffprobe -hide_banner -loglevel error -show_entries format=duration -of csv=p=0 $target) -as [double]
    # Long compilations must be split on silence before transcription/training.
    $status = if ($seconds -gt 120) { 'needs_split' } else { 'needs_transcript' }
    [pscustomobject]@{ utt = $utt; wav = ($target.Substring($outputPrefix.Length).Replace('\','/')); source = $relativeUnix; text = ''; status = $status }
})

$rows | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $OutputRoot 'manifest.json')
$rows | ConvertTo-Csv -NoTypeInformation | Set-Content -Encoding utf8 (Join-Path $OutputRoot 'manifest.csv')
$split = @($rows | Where-Object status -eq 'needs_split').Count
Write-Output "Prepared $($rows.Count) mono 24kHz WAV files ($split need silence-split first). Transcription is required before training. Output: $OutputRoot"
