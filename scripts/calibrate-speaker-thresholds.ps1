# VI-0 Speaker threshold calibration (docs/voice-identity.md, 07.10.2026).
# Embeds the owner's reference dataset (artifacts/cosyvoice-dataset/clips)
# through the EXISTING voice-service /voice/speaker/embed endpoint and computes
# the same-speaker cosine distribution, plus reference points against other
# voices. Read-only: no profiles are written, nothing is enrolled.
#
# Usage: powershell -File scripts/calibrate-speaker-thresholds.ps1 [-Sample 250]
param(
    [int]$Sample = 250,
    [string]$VoiceUrl = "http://127.0.0.1:9660",
    [string]$DatasetDir = "artifacts/cosyvoice-dataset"
)

$ErrorActionPreference = "Stop"
$clips = Get-ChildItem "$DatasetDir\clips" -Filter *.wav -File | Sort-Object Name
if ($clips.Count -eq 0) { throw "no clips in $DatasetDir\clips" }
if ($Sample -gt 0 -and $clips.Count -gt $Sample) {
    $step = [math]::Floor($clips.Count / $Sample)
    $clips = for ($i = 0; $i -lt $clips.Count; $i += $step) { $clips[$i] }
}
"embedding $($clips.Count) of $((Get-ChildItem "$DatasetDir\clips" -Filter *.wav).Count) clips..."

$embeddings = @()
foreach ($clip in $clips) {
    try {
        $resp = Invoke-WebRequest -Uri "$VoiceUrl/voice/speaker/embed" -Method POST -Form @{ file = $clip } -UseBasicParsing -TimeoutSec 30
        $json = $resp.Content | ConvertFrom-Json
        if ($json.embedding -and $json.embedding.Count -gt 0) { $embeddings += ,($json.embedding -as [double[]]) }
    } catch { Write-Host "skip $($clip.Name): $($_.Exception.Message)" }
}
"embedded ok: $($embeddings.Count)"

function Cosine([double[]]$a, [double[]]$b) {
    $dot = 0.0; $na = 0.0; $nb = 0.0
    for ($i = 0; $i -lt $a.Count; $i++) { $dot += $a[$i] * $b[$i]; $na += $a[$i] * $a[$i]; $nb += $b[$i] * $b[$i] }
    return [math]::Sqrt($dot * $dot) / [math]::Sqrt($na * $nb)
}

# Same-speaker spread: cosine of each clip to the dataset centroid.
$dim = $embeddings[0].Count
$mean = New-Object double[] $dim
foreach ($e in $embeddings) { for ($i = 0; $i -lt $dim; $i++) { $mean[$i] += $e[$i] / $embeddings.Count } }
$mn = [math]::Sqrt(($mean | Measure-Object -Sum { $_ * $_ }).Sum)
for ($i = 0; $i -lt $dim; $i++) { $mean[$i] /= $mn }

$toMean = $embeddings | ForEach-Object { Cosine $_ $mean }
$sorted = $toMean | Sort-Object
$pct = { param($p) $sorted[[int][math]::Floor(($sorted.Count - 1) * $p / 100.0)] }

# Pairwise sample (random 2000 pairs) for clip-to-clip spread.
$pairs = New-Object System.Collections.Generic.List[double]
$rand = [System.Random]::new(42)
for ($i = 0; $i -lt 2000 -and $embeddings.Count -gt 1; $i++) {
    $a = $rand.Next($embeddings.Count); $b = $rand.Next($embeddings.Count)
    if ($a -ne $b) { $pairs.Add((Cosine $embeddings[$a] $embeddings[$b])) }
}
$pairsSorted = $pairs | Sort-Object
$pairPct = { param($p) $pairsSorted[[int][math]::Floor(($pairsSorted.Count - 1) * $p / 100.0)] }

"=== SAME-SPEAKER distribution (cos to centroid, n=$($sorted.Count)) ==="
"min: {0:n4}  p1: {1:n4}  p5: {2:n4}  p50: {3:n4}  p95: {4:n4}" -f $sorted[0], (& $pct 1), (& $pct 5), (& $pct 50), (& $pct 95) | Write-Host
"=== SAME-SPEAKER pairwise (n=$($pairsSorted.Count)) ==="
"min: {0:n4}  p1: {1:n4}  p5: {2:n4}  p50: {3:n4}" -f $pairsSorted[0], (& $pairPct 1), (& $pairPct 5), (& $pairPct 50) | Write-Host

# Reference points: other voices vs the owner centroid.
foreach ($ref in @("voice_reference\ordis.mp3", "CosyVoice3\asset\zero_shot_prompt.wav", "CosyVoice3\asset\cross_lingual_prompt.wav")) {
    if (Test-Path $ref) {
        try {
            $resp = Invoke-WebRequest -Uri "$VoiceUrl/voice/speaker/embed" -Method POST -Form @{ file = (Get-Item $ref) } -UseBasicParsing -TimeoutSec 30
            $emb = ($resp.Content | ConvertFrom-Json).embedding -as [double[]]
            "{0} vs owner centroid: {1:n4}" -f $ref, (Cosine $emb $mean) | Write-Host
        } catch { Write-Host "$ref skip: $($_.Exception.Message)" }
    }
}
"=== Suggested tier boundaries (voice-identity.md 3) ==="
"Accept 0.45 | AdaptiveTier 0.60 | consistency 0.30 | distinctness 0.45"
"Verify: p5-to-centroid must sit comfortably above Accept; p1 above consistency floor."
