[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BackupPath,

    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]*$')]
    [string] $Database = 'tolik'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repositoryRoot 'docker-compose.yml'
$destination = [System.IO.Path]::GetFullPath($BackupPath)
$destinationDirectory = Split-Path -Parent $destination
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
}

$containerId = (& docker compose --file $composeFile ps --quiet postgres | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) {
    throw 'The PostgreSQL Compose service is not running.'
}

$temporaryName = "/tmp/tolik-backup-$PID.dump"
try {
    & docker compose --file $composeFile exec --no-TTY postgres pg_dump `
        --username=tolik --dbname=$Database --format=custom --file=$temporaryName
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE." }

    & docker compose --file $composeFile exec --no-TTY postgres pg_restore --list $temporaryName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The generated backup failed pg_restore integrity validation.' }

    & docker cp "${containerId}:$temporaryName" $destination
    if ($LASTEXITCODE -ne 0) { throw "docker cp failed with exit code $LASTEXITCODE." }

    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$destination.sha256" -Value "$hash  $([System.IO.Path]::GetFileName($destination))"
    Write-Output "Backup validated: $destination"
    Write-Output "SHA256: $hash"
}
finally {
    & docker compose --file $composeFile exec --no-TTY postgres rm -f -- $temporaryName 2>$null | Out-Null
}
