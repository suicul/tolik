# TOLIK stop script: stops the Docker stack (data in the Postgres volume is kept).
param([switch]$Full)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Full) {
    docker compose --profile vps down
} else {
    docker compose --profile vps stop
}
Write-Host "TOLIK stack stopped. Agent window(s) can be closed with Ctrl+C." -ForegroundColor Cyan
