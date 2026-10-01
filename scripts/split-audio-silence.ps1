# Splits long dataset WAVs on silence into short training clips (mono 24kHz).
# Reads manifest rows with status 'needs_split', replaces them with clip rows
# (status 'needs_transcript'). Intervals longer than MaxSeconds are cut at even
# midpoints (may clip words — transcripts come from the clip itself, and human
# review via apply-cosyvoice-transcripts.ps1 happens before training anyway).
[CmdletBinding()]
param(
    [string]$Manifest = 'artifacts/cosyvoice-dataset/manifest.csv',
    [double]$MinSeconds = 1.0,
    [double]$MaxSeconds = 20.0,
    [double]$SilenceSeconds = 0.5,
    [string]$SilenceDb = '-30dB',
    [string]$UttPrefix = 'ref'
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg not found in PATH.' }
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw 'ffprobe not found in PATH.' }
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root $Manifest
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Manifest not found: $manifestPath" }
$outRoot = Split-Path -Parent $manifestPath
$clipDir = Join-Path $outRoot 'clips'
New-Item -ItemType Directory -Force -Path $clipDir | Out-Null

# Native tools with fully captured streams. (Under $ErrorActionPreference=Stop
# PowerShell turns ANY native stderr text into a terminating error, so shell
# redirection is unreliable — silencedetect reports on stderr by design.)
function Invoke-Tool {
    param([string]$FileName, [string]$Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = $Arguments
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stderr = $process.StandardError.ReadToEnd()
    $stdout = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "$FileName failed (exit $($process.ExitCode)): $stderr" }
    return @{ StdOut = $stdout; StdErr = $stderr }
}

$rows = @(Import-Csv -LiteralPath $manifestPath)
$counter = 0
foreach ($row in $rows) {
    if ($row.utt -match "^$UttPrefix(\d+)$") { $counter = [math]::Max($counter, [int]$Matches[1]) }
}

$updated = New-Object System.Collections.Generic.List[object]
foreach ($row in $rows) {
    if ($row.status -ne 'needs_split') { $updated.Add($row); continue }
    $wavPath = $row.wav
    if (-not [IO.Path]::IsPathRooted($wavPath)) { $wavPath = Join-Path $outRoot $wavPath }
    $wavPath = [IO.Path]::GetFullPath($wavPath)
    if (-not (Test-Path -LiteralPath $wavPath)) { Write-Host "SKIP $($row.utt): wav missing ($wavPath)"; $updated.Add($row); continue }

    $probe = Invoke-Tool 'ffprobe' "-hide_banner -loglevel error -show_entries format=duration -of csv=p=0 `"$wavPath`""
    $duration = [double]($probe.StdOut.Trim())
    if ($duration -le 0) { Write-Host "SKIP $($row.utt): bad duration"; $updated.Add($row); continue }

    $detect = Invoke-Tool 'ffmpeg' "-hide_banner -nostats -i `"$wavPath`" -af `"silencedetect=noise=${SilenceDb}:d=${SilenceSeconds}`" -f null -"
    # Speech intervals sit between silence_start / silence_end markers.
    $bounds = New-Object System.Collections.Generic.List[object]
    $start = 0.0
    foreach ($line in ($detect.StdErr -split "`r?`n")) {
        if ($line -match 'silence_start:\s*([0-9.]+)') {
            $silenceStart = [double]$Matches[1]
            if ($silenceStart -gt $start) { $bounds.Add(@{ s = $start; e = $silenceStart }) }
        }
        elseif ($line -match 'silence_end:\s*([0-9.]+)') {
            $start = [double]$Matches[1]
        }
    }
    if ($duration -gt $start) { $bounds.Add(@{ s = $start; e = $duration }) }

    $kept = 0
    foreach ($b in $bounds) {
        $length = $b.e - $b.s
        if ($length -lt $MinSeconds) { continue }
        $cuts = [math]::Ceiling($length / $MaxSeconds)
        for ($i = 0; $i -lt $cuts; $i++) {
            $cs = [math]::Round($b.s + $length * $i / $cuts, 3)
            $ce = [math]::Round($b.s + $length * ($i + 1) / $cuts, 3)
            if (($ce - $cs) -lt $MinSeconds) { continue }
            $counter++
            $utt = '{0}{1:0000}' -f $UttPrefix, $counter
            $clip = Join-Path $clipDir ($utt + '.wav')
            [void](Invoke-Tool 'ffmpeg' "-hide_banner -loglevel error -y -i `"$wavPath`" -ss $cs -to $ce -ac 1 -ar 24000 -sample_fmt s16 `"$clip`"")
            $updated.Add([pscustomobject]@{ utt = $utt; wav = ('clips/' + $utt + '.wav'); source = $row.source; text = ''; status = 'needs_transcript' })
            $kept++
        }
    }
    Write-Host "Split $($row.utt): $kept clips from $([math]::Round($duration,1)) s"
}

$updated | ConvertTo-Csv -NoTypeInformation | Set-Content -LiteralPath $manifestPath -Encoding utf8
$updated | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($manifestPath, '.json')) -Encoding utf8
$needTranscript = @($updated | Where-Object { $_.status -eq 'needs_transcript' }).Count
Write-Output "Manifest now has $($updated.Count) rows ($needTranscript need transcripts)."
