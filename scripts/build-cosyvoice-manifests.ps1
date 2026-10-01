[CmdletBinding()]
param(
    [string]$Manifest = 'artifacts/cosyvoice-dataset/manifest.csv',
    [string]$OutputRoot = 'artifacts/cosyvoice-dataset',
    [double]$DevFraction = 0.1,
    [string]$Speaker = 'tolik_voice'
)
$ErrorActionPreference = 'Stop'
if ($DevFraction -le 0 -or $DevFraction -ge 0.5) { throw 'DevFraction must be > 0 and < 0.5.' }
$rows = Import-Csv $Manifest
if ($rows.Count -lt 10) { throw 'At least 10 labeled utterances are required.' }
$invalid = @($rows | Where-Object { [string]::IsNullOrWhiteSpace($_.text) -or $_.status -ne 'transcribed' })
if ($invalid.Count -gt 0) { throw "Transcription gate blocked: $($invalid.Count) rows are not marked transcribed." }
$missingIds = @($rows | Where-Object { [string]::IsNullOrWhiteSpace($_.utt) })
if ($missingIds.Count -gt 0) { throw "Manifest validation failed: $($missingIds.Count) rows have no utt identifier." }
$rng = [Random]::new(20260920)
$shuffled = @($rows | Sort-Object { $rng.Next() })
$devCount = [math]::Max(1, [math]::Floor($shuffled.Count * $DevFraction))
$dev = @($shuffled | Select-Object -First $devCount)
$train = @($shuffled | Select-Object -Skip $devCount)
foreach ($split in @(@('train',$train), @('dev',$dev))) {
    $name = $split[0]; $items = $split[1]; $root = Join-Path $OutputRoot $name
    New-Item -ItemType Directory -Force $root | Out-Null
    $items | ForEach-Object {
        $wavPath = $_.wav
        if (-not [IO.Path]::IsPathRooted($wavPath)) { $wavPath = Join-Path $OutputRoot $wavPath }
        "$($_.utt) $([IO.Path]::GetFullPath($wavPath))"
    } | Set-Content -Encoding utf8 (Join-Path $root 'wav.scp')
    $items | ForEach-Object { "$($_.utt) $($_.text)" } | Set-Content -Encoding utf8 (Join-Path $root 'text')
    $items | ForEach-Object { "$($_.utt) $Speaker" } | Set-Content -Encoding utf8 (Join-Path $root 'utt2spk')
}
Write-Output "Generated train=$($train.Count), dev=$($dev.Count) manifests under $OutputRoot"
