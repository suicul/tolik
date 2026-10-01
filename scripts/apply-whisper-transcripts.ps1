# Applies whisper transcripts (datasets/transcripts/manifest.jsonl) to the dataset
# manifest (artifacts/cosyvoice-dataset/manifest.csv): fills the text and marks
# rows needs_review. NEVER sets 'transcribed' — that status is the human-review
# gate (build-cosyvoice-manifests blocks everything else); whisper hallucinates
# on noisy clips, so review is mandatory before training (AGENTS.md §14).
# Also exports a tab-separated review sheet for human editing.
[CmdletBinding()]
param(
    [string]$Transcripts = 'datasets/transcripts/manifest.jsonl',
    [string]$Manifest = 'artifacts/cosyvoice-dataset/manifest.csv',
    [string]$ReviewTsv = 'artifacts/cosyvoice-dataset/review.tsv'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$transcriptsPath = Join-Path $root $Transcripts
$manifestPath = Join-Path $root $Manifest
if (-not (Test-Path -LiteralPath $transcriptsPath)) { throw "Transcripts not found: $transcriptsPath" }
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Manifest not found: $manifestPath" }

# utt comes from the clip file name (refNNNN.wav -> refNNNN).
$byUtt = @{}
foreach ($line in Get-Content -LiteralPath $transcriptsPath -Encoding utf8) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $entry = $line | ConvertFrom-Json
    $utt = [IO.Path]::GetFileNameWithoutExtension([string]$entry.wav)
    if ($utt -and -not $byUtt.ContainsKey($utt)) { $byUtt[$utt] = [string]$entry.text }
}

$rows = @(Import-Csv -LiteralPath $manifestPath -Encoding utf8)
$applied = 0
$missing = 0
foreach ($row in $rows) {
    if ($byUtt.ContainsKey($row.utt)) {
        $row.text = $byUtt[$row.utt]
        $row.status = 'needs_review'
        $applied++
    }
    elseif ($row.status -eq 'needs_transcript') { $missing++ }
}

$rows | ConvertTo-Csv -NoTypeInformation | Set-Content -LiteralPath $manifestPath -Encoding utf8
$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($manifestPath, '.json')) -Encoding utf8

$reviewPath = Join-Path $root $ReviewTsv
$rows | Where-Object { $_.status -eq 'needs_review' } |
    Select-Object utt, wav, text |
    Export-Csv -LiteralPath $reviewPath -Delimiter "`t" -NoTypeInformation -Encoding utf8

# Quality signals to size the review effort.
$short = @($rows | Where-Object { $_.status -eq 'needs_review' -and ([string]$_.text).Trim().Length -le 2 }).Count
Write-Output "Applied $applied transcripts (needs_review); $missing clips without a transcript."
Write-Output "Review sheet: $reviewPath"
Write-Output "Suspiciously short transcripts (<=2 chars): $short"
