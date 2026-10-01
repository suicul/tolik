[CmdletBinding()]
param(
    [string]$Manifest = 'artifacts/cosyvoice-dataset/manifest.csv',
    [Parameter(Mandatory = $true)][string]$TranscriptFile
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Manifest)) { throw "Manifest not found: $Manifest" }
if (-not (Test-Path -LiteralPath $TranscriptFile)) { throw "Transcript file not found: $TranscriptFile" }

$rows = @(Import-Csv -LiteralPath $Manifest)
$transcripts = @(Import-Csv -LiteralPath $TranscriptFile -Delimiter ([char]9))
if ($transcripts.Count -eq 0) { throw 'Transcript file is empty.' }

$byId = @{}
foreach ($item in $transcripts) {
    if ([string]::IsNullOrWhiteSpace($item.utt) -or [string]::IsNullOrWhiteSpace($item.text)) {
        throw 'Every transcript row must contain non-empty utt and text columns.'
    }
    if ($byId.ContainsKey($item.utt)) { throw "Duplicate utt in transcript file: $($item.utt)" }
    $byId[$item.utt] = $item.text.Trim()
}

$updated = 0
foreach ($row in $rows) {
    if ($byId.ContainsKey($row.utt)) {
        $row.text = $byId[$row.utt]
        $row.status = 'transcribed'
        $updated++
    }
}
if ($updated -eq 0) { throw 'No transcript IDs matched the manifest.' }
$rows | ConvertTo-Csv -NoTypeInformation | Set-Content -LiteralPath $Manifest -Encoding utf8
$jsonPath = [IO.Path]::ChangeExtension($Manifest, '.json')
$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $jsonPath -Encoding utf8
Write-Output "Applied $updated reviewed transcripts. Remaining unlabeled: $(@($rows | Where-Object status -ne 'transcribed').Count)."
