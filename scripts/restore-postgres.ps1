[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BackupPath,

    [Parameter(Mandatory)]
    [Alias('Database')]
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]*$')]
    [string] $TargetDatabase,

    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_]*$')]
    [string] $SourceDatabase = 'tolik',

    [ValidatePattern('^[a-zA-Z0-9_.:-]+$')]
    [string] $SentinelKey = 'backup_sentinel'
)

$ErrorActionPreference = 'Stop'
if ($TargetDatabase -eq $SourceDatabase) {
    throw 'Restore target must differ from the source database.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repositoryRoot 'docker-compose.yml'
$source = [System.IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Backup file does not exist: $source"
}

$checksumPath = "$source.sha256"
if (Test-Path -LiteralPath $checksumPath -PathType Leaf) {
    $expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
    $actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw 'Backup SHA256 checksum does not match.' }
}

$containerOutput = & docker compose --file $composeFile ps --quiet postgres | Out-String
$containerExitCode = $LASTEXITCODE
if ($containerExitCode -ne 0) {
    throw 'The PostgreSQL Compose service is not running.'
}
$containerId = $containerOutput.Trim()
if ([string]::IsNullOrWhiteSpace($containerId)) { throw 'The PostgreSQL Compose service is not running.' }

$temporaryName = "/tmp/tolik-restore-$PID.dump"
& docker cp $source "${containerId}:$temporaryName"
if ($LASTEXITCODE -ne 0) { throw "docker cp failed with exit code $LASTEXITCODE." }

$targetCreated = $false
try {
    # Validate before any database mutation. A corrupt input cannot drop or create a database.
    & docker compose --file $composeFile exec --no-TTY postgres pg_restore --list $temporaryName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Backup is not a valid PostgreSQL custom-format archive.' }

    $targetExistsOutput = & docker compose --file $composeFile exec --no-TTY postgres psql `
        --username=tolik --dbname=postgres --tuples-only --no-align --set=ON_ERROR_STOP=1 `
        --command="SELECT count(*) FROM pg_database WHERE datname = '$TargetDatabase';" | Out-String
    $targetExistsExitCode = $LASTEXITCODE
    if ($targetExistsExitCode -ne 0) { throw "Target database existence check failed with exit code $targetExistsExitCode." }
    $targetExists = $targetExistsOutput.Trim()
    if ($targetExists -ne '0') {
        throw "Restore target database already exists: $TargetDatabase"
    }

    & docker compose --file $composeFile exec --no-TTY postgres createdb `
        --username=tolik $TargetDatabase
    $createExitCode = $LASTEXITCODE
    if ($createExitCode -ne 0) { throw "createdb failed with exit code $createExitCode." }
    $targetCreated = $true

    & docker compose --file $composeFile exec --no-TTY postgres pg_restore `
        --username=tolik --dbname=$TargetDatabase --exit-on-error --no-owner --no-privileges $temporaryName
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }

    $migrationOutput = & docker compose --file $composeFile exec --no-TTY postgres psql `
        --username=tolik --dbname=$TargetDatabase --tuples-only --no-align --set=ON_ERROR_STOP=1 `
        --command='SELECT count(*) FROM "__EFMigrationsHistory";' | Out-String
    $migrationExitCode = $LASTEXITCODE
    if ($migrationExitCode -ne 0) { throw "Migration history verification failed with exit code $migrationExitCode." }
    $migrationCount = $migrationOutput.Trim()
    if (-not [int]::TryParse($migrationCount, [ref] $null) -or [int]$migrationCount -lt 1) {
        throw 'Restored database has no Entity Framework migration history.'
    }

    $sentinelOutput = & docker compose --file $composeFile exec --no-TTY postgres psql `
        --username=tolik --dbname=$TargetDatabase --tuples-only --no-align --set=ON_ERROR_STOP=1 `
        --command="SELECT count(*) FROM system_records WHERE key = '$SentinelKey';" | Out-String
    $sentinelExitCode = $LASTEXITCODE
    if ($sentinelExitCode -ne 0) { throw "Sentinel verification failed with exit code $sentinelExitCode." }
    $sentinelCount = $sentinelOutput.Trim()
    if (-not [int]::TryParse($sentinelCount, [ref] $null) -or [int]$sentinelCount -ne 1) {
        throw "Restored database is missing sentinel record '$SentinelKey'."
    }

    Write-Output "Restore validated: database=$TargetDatabase migrations=$migrationCount sentinel=$SentinelKey"
}
catch {
    if ($targetCreated) {
        & docker compose --file $composeFile exec --no-TTY postgres dropdb `
            --username=tolik --if-exists --force $TargetDatabase | Out-Null
    }
    throw
}
finally {
    & docker compose --file $composeFile exec --no-TTY postgres rm -f -- $temporaryName 2>$null | Out-Null
}
