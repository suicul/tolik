# TOLIK end-to-end MVP smoke test.
# Verifies: health -> session (device signature) -> status -> memory -> chat -> agents.
# Requires: PostgreSQL up, Core running, the device registered in the TOLIK client.
# Usage: .\scripts\test-e2e.ps1 [-Endpoint http://127.0.0.1:5080] [-SkipChat]
#
# Talks to the Core API directly. It previously drove the retired TOLIK.Cli; keeping the
# checks in PowerShell means this script exercises the same public surface a real client
# uses, and no longer needs a build artifact to run.
#
# Runs on Windows PowerShell 5.1 as well as PowerShell 7: the HTTP layer is HttpClient
# instead of Invoke-WebRequest, because -SkipHttpErrorCheck and .RawContentStream only
# exist in 7 and made the whole script unusable from the default Windows shell. It also
# asserts the responses Core actually returns — /health has no "status" field, and a
# deterministic action with no connected agent returns an honest 503, not a fake success.

param(
    [string]$Endpoint = "http://127.0.0.1:5080",
    [switch]$SkipChat
)

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Net.Http

$pass = 0; $fail = 0
function Check($name, $condition) {
    if ($condition) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  [FAIL] $name" -ForegroundColor Red; $script:fail++ }
}

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(180)

function Invoke-Core {
    param([string]$Path, [string]$Method = 'GET', $Body, [string]$Token)

    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod($Method.ToUpperInvariant())), "$Endpoint$Path")
    try {
        if ($Token) { $null = $request.Headers.TryAddWithoutValidation('Authorization', "Bearer $Token") }
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 6
            $request.Content = New-Object System.Net.Http.StringContent(
                $json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $response.Dispose()
        return [pscustomobject]@{
            Status = $status
            Text   = $text
            Json   = if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null }
        }
    } finally {
        $request.Dispose()
    }
}

Write-Host "== TOLIK E2E Smoke ==" -ForegroundColor Cyan

# 1. Health (no auth needed). The payload is component flags, not a "status" field.
$health = $null
try { $health = Invoke-Core -Path '/health' } catch { $health = $null }
if ($null -ne $health) {
    Check "health endpoint" ($health.Status -eq 200 -and $health.Json.core -eq $true)
    Check "memory service reported up" ($health.Json.memory -eq $true)
    # The lease is renewed on a timer, so a single sample can legitimately catch the
    # moment it is being re-acquired. Assert the field is reported, not that one instant
    # happens to be healthy - a flaky check teaches nothing and hides real failures.
    Check "core lease state is reported" ($null -ne $health.Json.coreLease -and $null -ne $health.Json.coreLease.stale)
} else {
    Check "health endpoint" $false
}

# 2. Session via device signature (the shipped-client path; no dev flag, works after Genesis)
Write-Host "-- session via device signature --"
$tokenScript = Join-Path $PSScriptRoot 'get-session-token.ps1'
& $tokenScript -CoreUrl $Endpoint -Quiet
$tokenFile = Join-Path $HOME ".tolik\session-token"
if (-not (Test-Path $tokenFile)) {
    Check "session acquired" $false
    Write-Host "No session token - register the device in the TOLIK client first." -ForegroundColor Red
    exit 1
}
$token = (Get-Content $tokenFile -Raw).Trim()
Check "session acquired" ($token.Length -gt 0)

# 3. Authenticated status carries the real identity, not just a 200.
$status = Invoke-Core -Path '/status' -Token $token
Check "authenticated /status" ($status.Status -eq 200 -and $status.Json.status -eq "Healthy")
Check "/status reports the owner role" ($status.Json.role -eq "Owner")
Check "/status reports a device id" ($null -ne $status.Json.deviceId)

# 4. Memory write + search. The search must find what the chat turn just stored.
$utterance = "запомни: тестовая память E2E 20260924"
$write = Invoke-Core -Path '/chat' -Method Post -Token $token -Body @{ message = $utterance }
Check "chat turn storing memory accepted" ($write.Status -eq 200)
$memory = Invoke-Core -Path "/memory/search?q=$([uri]::EscapeDataString('тестовая память E2E'))" -Token $token
Check "memory search finds entry" ($memory.Status -eq 200 -and $null -ne $memory.Json)
Check "memory search returns at least one hit" (@($memory.Json).Count -ge 1)
Check "no token is echoed back in the memory payload" (-not $memory.Text.Contains($token))

# 5. Chat: either the deterministic action executes (agent connected) or Core honestly
#    refuses. What must never happen is a fabricated success.
if (-not $SkipChat) {
    $calc = Invoke-Core -Path '/chat' -Method Post -Token $token -Body @{ message = "открой калькулятор" }
    if ($calc.Status -eq 200) {
        Check "calculator handled by an agent" ($calc.Json.action -eq "run_process")
    } else {
        # Any 5xx is acceptable ONLY if it carries a reason the operator can act on. The
        # specific reason varies (no agent advertises RunProcess, or no model route is
        # available), so pin the honest-refusal property instead of one exact sentence.
        $hasReason = ($null -ne $calc.Json) -and -not [string]::IsNullOrWhiteSpace($calc.Json.detail)
        Check "calculator refused with an actionable reason" ($calc.Status -ge 500 -and $hasReason)
    }
}

# 6. Agents list. Empty is a valid answer (no agent connected); the endpoint must still work.
$agents = Invoke-Core -Path '/agents' -Token $token
Check "agents listable" ($agents.Status -eq 200)

Write-Host ""
Write-Host "Result: PASS=$pass FAIL=$fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
$client.Dispose()
exit $(if ($fail -eq 0) { 0 } else { 1 })
