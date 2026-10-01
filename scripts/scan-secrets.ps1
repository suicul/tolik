[CmdletBinding()]
param(
    [Parameter()]
    [string]$Source,

    [Parameter()]
    [switch]$VerboseOutput
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $PSScriptRoot ".."
}

$image = "zricethezav/gitleaks@sha256:e1b35e12a8c6fa8901f060459cfb6b2fc4c484d3afbe3b029733a3bbfab07055"
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$configPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\\.gitleaks.toml")).Path

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is required to run the pinned Gitleaks scanner image."
}

$dockerArguments = @(
    "run", "--rm",
    "--volume", "${sourcePath}:/scan:ro",
    "--volume", "${configPath}:/gitleaks/.gitleaks.toml:ro",
    $image,
    "dir", "/scan",
    "--config", "/gitleaks/.gitleaks.toml",
    "--no-banner",
    "--no-color",
    "--redact=100",
    "--exit-code", "1"
)

if ($VerboseOutput) {
    $dockerArguments += "--verbose"
}

& docker @dockerArguments
exit $LASTEXITCODE
