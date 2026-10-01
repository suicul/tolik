# Applies English transcripts (metadata-en.csv, utt|text|text per line) to the
# dataset manifest: wholesale text replacement, status stays needs_review.
# Wholesale is justified: the source is 100% English Ordis lines, so the
# previous Russian-forced texts are contamination, not data (provenance backup
# kept next to the manifest). Review gate still applies before training.
[CmdletBinding()]
param(
    [string]$MetadataEn = 'artifacts/cosyvoice-dataset/metadata-en.csv',
    [string]$Manifest = 'artifacts/cosyvoice-dataset/manifest.csv',
    [string]$ReviewTsv = 'artifacts/cosyvoice-dataset/review.tsv'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$metaPath = Join-Path $root $MetadataEn
$manifestPath = Join-Path $root $Manifest
if (-not (Test-Path -LiteralPath $metaPath)) { throw "Metadata not found: $metaPath" }
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Manifest not found: $manifestPath" }

$byUtt = @{}
foreach ($line in Get-Content -LiteralPath $metaPath -Encoding utf8) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split '\|', 3
    if ($parts.Count -lt 2 -or [string]::IsNullOrWhiteSpace($parts[0])) { continue }
    $byUtt[$parts[0].Trim()] = $parts[1].Trim()
}

$backup = [IO.Path]::ChangeExtension($manifestPath, '.ru-soup.csv')
Copy-Item -LiteralPath $manifestPath -Destination $backup -Force

$rows = @(Import-Csv -LiteralPath $manifestPath -Encoding utf8)
$replaced = 0
$missing = 0
foreach ($row in $rows) {
    if ($byUtt.ContainsKey($row.utt)) {
        $row.text = $byUtt[$row.utt]
        $row.status = 'needs_review'
        $replaced++
    }
    elseif ([string]::IsNullOrWhiteSpace($row.text)) { $missing++ }
}

$rows | ConvertTo-Csv -NoTypeInformation | Set-Content -LiteralPath $manifestPath -Encoding utf8
$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($manifestPath, '.json')) -Encoding utf8

$reviewPath = Join-Path $root $ReviewTsv
$rows | Where-Object { $_.status -eq 'needs_review' } |
    Select-Object utt, wav, text |
    Export-Csv -LiteralPath $reviewPath -Delimiter "`t" -NoTypeInformation -Encoding utf8

$short = @($rows | Where-Object { $_.status -eq 'needs_review' -and ([string]$_.text).Trim().Length -le 2 }).Count
$cyr = @($rows | Where-Object { $_.status -eq 'needs_review' -and $_.text -match '[\u0400-\u04FF]' }).Count
Write-Output "Replaced $replaced texts (backup: $backup); empty: $missing; short: $short; still cyrillic: $cyr."
Write-Output "Review sheet: $reviewPath"
