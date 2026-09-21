<#
.SYNOPSIS
  Builds a runnable LOCAL development StreetBizDB from the authoritative schema.

.DESCRIPTION
  StreetBizDB is database-first: the API never migrates or creates it
  (docs/database.md). This script applies, in order, the only two database scripts:

    1. db/StreetBiz_SQL_Server.sql  schema (tables, triggers, views, every CHECK
                                    constraint), reference data (roles, wards,
                                    violation types) and the __EFMigrationsHistory stamp
    2. db/StreetBiz_Demo_Seed.sql   demo accounts and the full scenario (skipped
                                    with -SkipDemoSeed)

  Schema changes are edited into those two files, never added as new scripts --
  see the maintenance rules at the top of db/StreetBiz_SQL_Server.sql.

  It also writes the placeholder evidence files the seeded RegistrationEvidence
  rows point at, so the ward reviewer's document preview works.

  Earlier versions generated the schema from the EF model with
  `dotnet ef dbcontext script`. That output silently omitted all CHECK constraints,
  both views and all five triggers, so the local database accepted data the real
  one rejects. It is no longer used; App_Data/dev-schema.sql is a stale artifact.

  Stop the API before running: an open connection blocks DROP DATABASE.

.PARAMETER Recreate
  Drop and rebuild. Without it the script refuses to touch a database that
  already has tables.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Recreate

.PARAMETER SqlUser
  SQL login for a server that has no Windows authentication, e.g. SQL Server in
  Docker (use 'sa'). Needs -SqlPassword and -AllowNonLocalDb.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Server ".\SQLEXPRESS" -AllowNonLocalDb -Recreate

.EXAMPLE
  # SQL Server in Docker (docker-compose.yml, port 1433)
  powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Server "localhost,1433" -SqlUser sa -SqlPassword "<SQLSERVER_SA_PASSWORD>" -AllowNonLocalDb -Recreate
#>
param(
    [string]$Server = '(localdb)\MSSQLLocalDB',
    [string]$Database = 'StreetBizDB',
    [switch]$Recreate,
    [switch]$AllowNonLocalDb,
    [switch]$SkipDemoSeed,
    [string]$SqlUser,
    [string]$SqlPassword
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $AllowNonLocalDb -and $Server -notlike '(localdb)*') {
    throw "Refusing to touch '$Server'. This script is for LocalDB; pass -AllowNonLocalDb if you really mean it."
}

function Invoke-Sql([string]$connectionString, [string]$sql, [string]$label) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()
    try {
        # SqlConnection sets QUOTED_IDENTIFIER ON by default, which the filtered
        # indexes need; the scripts also state it explicitly for sqlcmd's benefit.
        foreach ($batch in [regex]::Split($sql, '(?im)^\s*GO\s*$')) {
            if ([string]::IsNullOrWhiteSpace($batch)) { continue }
            $command = $connection.CreateCommand()
            $command.CommandText = $batch
            $command.CommandTimeout = 180
            [void]$command.ExecuteNonQuery()
        }
    } catch {
        throw "Failed while applying $label`: $($_.Exception.Message)"
    } finally {
        $connection.Close()
    }
}

function Get-Scalar([string]$connectionString, [string]$sql) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        return $command.ExecuteScalar()
    } finally {
        $connection.Close()
    }
}

function Invoke-SqlFile([string]$connectionString, [string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path $path)) { throw "Missing required script: $relativePath" }
    Write-Host "  -> $relativePath"
    Invoke-Sql $connectionString (Get-Content $path -Raw -Encoding UTF8) $relativePath
}

if ($Server -like '(localdb)*') {
    Write-Host "Starting LocalDB..."
    sqllocaldb start ($Server -replace '^\(localdb\)\\', '') | Out-Null
}

$auth = if ($SqlUser) { "User Id=$SqlUser;Password=$SqlPassword" } else { 'Trusted_Connection=True' }
$master = "Server=$Server;Database=master;$auth;TrustServerCertificate=True"
$target = "Server=$Server;Database=$Database;$auth;TrustServerCertificate=True"

# DB_ID returns DBNull (which PowerShell treats as true) when the database is missing.
$databaseId = Get-Scalar $master "SELECT DB_ID(N'$Database')"
$exists = -not ($null -eq $databaseId -or $databaseId -is [System.DBNull])

if ($exists) {
    $tableCount = Get-Scalar $target "SELECT COUNT(*) FROM sys.tables WHERE name <> '__EFMigrationsHistory'"
    if ($tableCount -gt 0 -and -not $Recreate) {
        throw "$Database already has $tableCount table(s). Pass -Recreate to drop and rebuild it. THIS DESTROYS ALL DATA -- back up first:`n  sqlcmd -S `"$Server`" -E -Q `"BACKUP DATABASE [$Database] TO DISK='C:\Temp\$Database.bak' WITH INIT`""
    }
    if ($Recreate) {
        Write-Host "Dropping $Database..." -ForegroundColor Yellow
        # Rolls back and disconnects everything else; without it DROP blocks on any open session.
        Invoke-Sql $master "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;" 'drop (single-user)'
        Invoke-Sql $master "DROP DATABASE [$Database];" 'drop'
        $exists = $false
    }
}

if (-not $exists) {
    Write-Host "Creating database $Database..."
    Invoke-Sql $master "CREATE DATABASE [$Database];" 'create database'
}

Write-Host "Applying schema and data..."
Invoke-SqlFile $target 'db\StreetBiz_SQL_Server.sql'
if (-not $SkipDemoSeed) {
    Invoke-SqlFile $target 'db\StreetBiz_Demo_Seed.sql'
}

# ---------------------------------------------------------------------------
# Placeholder evidence files.
#
# The seeded RegistrationEvidence rows point at real URLs, so the ward reviewer's
# document preview needs bytes on disk at Storage:RootPath/evidence/{userId}/{name}
# (EvidenceFiles.StoragePath). These are 1x1 images and a one-page PDF -- just
# enough for the magic-byte check in EvidenceFiles.DetectExtension to pass.
# ---------------------------------------------------------------------------
if (-not $SkipDemoSeed) {
    Write-Host "Writing placeholder evidence files..."

    $uploads = Join-Path $root 'src\StreetBiz.API\App_Data\uploads\evidence'

    # 1x1 white JPEG.
    $jpegBase64 = '/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q=='
    # Minimal valid one-page PDF.
    $pdfText = @"
%PDF-1.4
1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj
trailer<</Root 1 0 R>>
%%EOF
"@

    $evidenceFiles = @(
        @{ User = 5; Name = '0000000000000000000000000000ab01.jpg'; Kind = 'jpg' },
        @{ User = 5; Name = '0000000000000000000000000000ab02.pdf'; Kind = 'pdf' },
        @{ User = 5; Name = '0000000000000000000000000000ab03.pdf'; Kind = 'pdf' },
        @{ User = 6; Name = '0000000000000000000000000000ab04.jpg'; Kind = 'jpg' },
        @{ User = 8; Name = '0000000000000000000000000000ab05.jpg'; Kind = 'jpg' },
        @{ User = 8; Name = '0000000000000000000000000000ab06.jpg'; Kind = 'jpg' }
    )

    foreach ($file in $evidenceFiles) {
        # EvidenceFiles.IsValidFileName demands exactly 32 hex characters + extension.
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        if ($stem -notmatch '^[a-f0-9]{32}$') {
            throw "Evidence file name '$($file.Name)' is not 32 hex characters; the API would reject its URL."
        }

        $dir = Join-Path $uploads $file.User
        New-Item -ItemType Directory -Force $dir | Out-Null
        $path = Join-Path $dir $file.Name
        if (Test-Path $path) { continue }

        if ($file.Kind -eq 'jpg') {
            [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($jpegBase64))
        } else {
            [System.IO.File]::WriteAllText($path, $pdfText, (New-Object System.Text.ASCIIEncoding))
        }
    }
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== $Database on $Server ===" -ForegroundColor Green

$summary = Get-Scalar $target @"
SELECT CONCAT(
    'tables=',    (SELECT COUNT(*) FROM sys.tables),
    '  triggers=',(SELECT COUNT(*) FROM sys.triggers WHERE is_ms_shipped = 0),
    '  views=',   (SELECT COUNT(*) FROM sys.views    WHERE is_ms_shipped = 0),
    '  checks=',  (SELECT COUNT(*) FROM sys.check_constraints),
    '  migrations=', (SELECT COUNT(*) FROM [__EFMigrationsHistory]))
"@
Write-Host $summary

$wards = Get-Scalar $target "SELECT COUNT(*) FROM AdministrativeUnits WHERE unit_type = 'WARD'"
Write-Host "Wards available: $wards"

if (-not $SkipDemoSeed) {
    $rows = Get-Scalar $target @"
SELECT CONCAT(
    'accounts=',      (SELECT COUNT(*) FROM UserAccounts),
    '  registrations=',(SELECT COUNT(*) FROM BusinessRegistrations),
    '  slots=',       (SELECT COUNT(*) FROM SidewalkSlots),
    '  contracts=',   (SELECT COUNT(*) FROM RentalContracts),
    '  storefronts=', (SELECT COUNT(*) FROM Storefronts),
    '  orders=',      (SELECT COUNT(*) FROM Orders))
"@
    Write-Host $rows
    Write-Host ""
    Write-Host "Demo accounts -- password for all: Password123!" -ForegroundColor Cyan
    Write-Host "  0900000001  PLATFORM_ADMIN"
    Write-Host "  0983000001  WARD_AUTHORITY (ward 10)   <- used by scripts/e2e-auth-onboarding.sh"
    Write-Host "  0905000101  VENDOR (approved, has contract + storefront)"
    Write-Host "  0905000201  CUSTOMER"
    Write-Host "  Full list: docs/database.md"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
