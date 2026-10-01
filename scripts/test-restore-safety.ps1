[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repositoryRoot 'docker-compose.yml'
$restoreScript = Join-Path $PSScriptRoot 'restore-postgres.ps1'
$projectName = "tolikrestore$([guid]::NewGuid().ToString('N'))"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "tolik-restore-safety-$([guid]::NewGuid().ToString('N'))"
$previousProjectName = $env:COMPOSE_PROJECT_NAME
$previousPassword = $env:TOLIK_POSTGRES_PASSWORD
$previousPort = $env:TOLIK_POSTGRES_PORT
$env:COMPOSE_PROJECT_NAME = $projectName
$env:TOLIK_POSTGRES_PASSWORD = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:TOLIK_POSTGRES_PORT = '0'

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    & docker compose --file $composeFile --project-name $projectName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}

function Invoke-RestoreExpectFailure {
    param(
        [Parameter(Mandatory)][string] $BackupPath,
        [Parameter(Mandatory)][string] $TargetDatabase,
        [Parameter(Mandatory)][string] $ExpectedMessage
    )

    $output = & pwsh -NoProfile -File $restoreScript -BackupPath $BackupPath -TargetDatabase $TargetDatabase 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        throw "Restore unexpectedly succeeded for target '$TargetDatabase'."
    }
    if ($output -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "Restore failed without expected message '$ExpectedMessage'. Output: $output"
    }
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$missingSentinelArchive = Join-Path $temporaryRoot 'missing-sentinel.dump'
$validArchive = Join-Path $temporaryRoot 'valid.dump'
$corruptArchive = Join-Path $temporaryRoot 'corrupt.dump'

try {
    Write-Output "SETUP project=$projectName"
    Invoke-Compose up --detach --wait postgres

    Invoke-Compose exec --no-TTY postgres psql --username=tolik --dbname=tolik --set=ON_ERROR_STOP=1 --command='CREATE TABLE "__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY); INSERT INTO "__EFMigrationsHistory" VALUES (''20260919_restore_safety''); CREATE TABLE system_records (key text PRIMARY KEY, value text NOT NULL);'
    Invoke-Compose exec --no-TTY postgres pg_dump --username=tolik --dbname=tolik --format=custom --file=/tmp/missing-sentinel.dump
    $containerId = (& docker compose --file $composeFile --project-name $projectName ps --quiet postgres | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) { throw 'Could not resolve disposable PostgreSQL container.' }
    & docker cp "${containerId}:/tmp/missing-sentinel.dump" $missingSentinelArchive
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy missing-sentinel archive from disposable container.' }

    Invoke-Compose exec --no-TTY postgres psql --username=tolik --dbname=tolik --set=ON_ERROR_STOP=1 --command="INSERT INTO system_records VALUES ('backup_sentinel', 'present');"
    Invoke-Compose exec --no-TTY postgres pg_dump --username=tolik --dbname=tolik --format=custom --file=/tmp/valid.dump
    & docker cp "${containerId}:/tmp/valid.dump" $validArchive
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy valid archive from disposable container.' }
    Set-Content -LiteralPath $corruptArchive -Value 'not a PostgreSQL archive' -NoNewline

    Invoke-Compose exec --no-TTY postgres createdb --username=tolik guard_target
    Invoke-Compose exec --no-TTY postgres psql --username=tolik --dbname=guard_target --set=ON_ERROR_STOP=1 --command='CREATE TABLE restore_guard (value text PRIMARY KEY); INSERT INTO restore_guard VALUES (''preserved'');'

    $existingTargetOutput = & pwsh -NoProfile -File $restoreScript -BackupPath $missingSentinelArchive -TargetDatabase guard_target 2>&1 | Out-String
    $existingTargetExitCode = $LASTEXITCODE
    if ($existingTargetExitCode -eq 0) { throw "Restore unexpectedly succeeded for existing target 'guard_target'." }
    $guardDatabaseCountOutput = & docker compose --file $composeFile --project-name $projectName exec --no-TTY postgres psql --username=tolik --dbname=postgres --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT count(*) FROM pg_database WHERE datname = 'guard_target';" | Out-String
    $guardDatabaseCountExitCode = $LASTEXITCODE
    if ($guardDatabaseCountExitCode -ne 0) { throw "Could not verify guard target existence (exit $guardDatabaseCountExitCode)." }
    $guardDatabaseCount = $guardDatabaseCountOutput.Trim()
    if ($guardDatabaseCount -ne '1') { throw "Existing target database was dropped; observed count '$guardDatabaseCount'." }
    $guardValueOutput = & docker compose --file $composeFile --project-name $projectName exec --no-TTY postgres psql --username=tolik --dbname=guard_target --tuples-only --no-align --set=ON_ERROR_STOP=1 --command='SELECT value FROM restore_guard;' | Out-String
    $guardQueryExitCode = $LASTEXITCODE
    if ($guardQueryExitCode -ne 0) { throw "Could not query existing target guard row (exit $guardQueryExitCode)." }
    $guardValue = $guardValueOutput.Trim()
    if ($guardValue -ne 'preserved') {
        throw "Existing target guard row was lost; observed '$guardValue'."
    }
    if ($existingTargetOutput -notmatch [regex]::Escape('Restore target database already exists')) {
        throw "Existing target was preserved but restore did not report the safety refusal. Output: $existingTargetOutput"
    }
    Write-Output 'PASS existing target rejected and guard row preserved'

    & pwsh -NoProfile -File $restoreScript -BackupPath $validArchive -TargetDatabase fresh_target
    if ($LASTEXITCODE -ne 0) { throw "Fresh restore failed with exit code $LASTEXITCODE." }
    $freshCheck = (& docker compose --file $composeFile --project-name $projectName exec --no-TTY postgres psql --username=tolik --dbname=fresh_target --tuples-only --no-align --set=ON_ERROR_STOP=1 --command='SELECT (SELECT count(*) FROM "__EFMigrationsHistory")::text || '':'' || (SELECT value FROM system_records WHERE key = ''backup_sentinel'');' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $freshCheck -ne '1:present') {
        throw "Fresh restore verification failed; observed '$freshCheck'."
    }
    Write-Output 'PASS fresh target restored with migration and sentinel'

    Invoke-RestoreExpectFailure -BackupPath $corruptArchive -TargetDatabase corrupt_target -ExpectedMessage 'Backup is not a valid PostgreSQL custom-format archive'
    $corruptExists = (& docker compose --file $composeFile --project-name $projectName exec --no-TTY postgres psql --username=tolik --dbname=postgres --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT count(*) FROM pg_database WHERE datname = 'corrupt_target';" | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $corruptExists -ne '0') {
        throw "Corrupt archive created a target database; observed count '$corruptExists'."
    }
    Write-Output 'PASS corrupt archive rejected before target creation'
}
finally {
    Write-Output "TEARDOWN project=$projectName"
    & docker compose --file $composeFile --project-name $projectName down --volumes --remove-orphans 2>&1 | Out-Null
    $cleanupExitCode = $LASTEXITCODE

    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }

    $env:COMPOSE_PROJECT_NAME = $previousProjectName
    $env:TOLIK_POSTGRES_PASSWORD = $previousPassword
    $env:TOLIK_POSTGRES_PORT = $previousPort

    if ($cleanupExitCode -ne 0) {
        throw "Disposable Compose teardown failed with exit code $cleanupExitCode."
    }
}
