# TOLIK migration rollback (docs/backup-recovery.md gap #4).
# DESTRUCTIVE operation (S11): rolling back drops schema objects AND their data
# (e.g. AddMemoryOrigin drops tolik_memories.Origin + its values). Requires an
# explicit -ConfirmRollback flag + shows the current state first. Run from the
# repository root; targets the SAME database as TOLIK_POSTGRES_CONNECTION / config.
param(
    [Parameter(Mandatory = $true)][string]$TargetMigration,
    [switch]$ConfirmRollback
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path ".git")) { Write-Error "Run from the repository root (.git not found)."; exit 1 }
if (-not $ConfirmRollback) {
    Write-Host "DESTRUCTIVE: this drops schema objects and their data down to '$TargetMigration'."
    Write-Host "Current applied migrations:"
    dotnet ef migrations list --project src\TOLIK.Infrastructure\TOLIK.Infrastructure.csproj --no-connect 2>$null | Select-Object -Last 8
    Write-Host "Re-run with -ConfirmRollback to execute."
    exit 2
}

Write-Host "Rolling back to '$TargetMigration'..."
dotnet ef database update $TargetMigration --project src\TOLIK.Infrastructure\TOLIK.Infrastructure.csproj --startup-project src\TOLIK.Infrastructure\TOLIK.Infrastructure.csproj
if ($LASTEXITCODE -ne 0) { Write-Error "Rollback failed."; exit 1 }

Write-Host "Rollback complete. Applied migrations now:"
dotnet ef migrations list --project src\TOLIK.Infrastructure\TOLIK.Infrastructure.csproj --startup-project src\TOLIK.Infrastructure\TOLIK.Infrastructure.csproj 2>$null | Select-Object -Last 5
Write-Host "NOTE: re-apply later with 'dotnet ef database update' (data dropped by the rollback is NOT recovered)."
