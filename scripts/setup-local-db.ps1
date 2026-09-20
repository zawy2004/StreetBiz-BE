<#
.SYNOPSIS
  Creates a runnable LOCAL development StreetBizDB for Authentication and Vendor Onboarding.

.DESCRIPTION
  The repository has no schema DDL: StreetBizDB is database-first and the
  InitialBaseline migration is an intentional no-op (docs/migration-guide.md).
  For local development this script:

    1. generates CREATE TABLE statements from the scaffolded EF model
       (dotnet ef dbcontext script) into App_Data/dev-schema.sql;
    2. creates the database if it does not exist;
    3. applies the schema only when the database has no user tables;
    4. runs docs/dev-seed.sql (roles + wards).

  The generated schema omits the database's CHECK constraints, triggers and the two
  views, so it is for local development only. The script refuses any server other
  than LocalDB unless -AllowNonLocalDb is passed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1
#>
param(
    [string]$Server = '(localdb)\MSSQLLocalDB',
    [string]$Database = 'StreetBizDB',
    [switch]$AllowNonLocalDb
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $AllowNonLocalDb -and $Server -notlike '(localdb)*') {
    throw "Refusing to touch '$Server'. This script is for LocalDB; pass -AllowNonLocalDb if you really mean it."
}

function Invoke-Sql([string]$connectionString, [string]$sql) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()
    try {
        foreach ($batch in [regex]::Split($sql, '(?im)^\s*GO\s*$')) {
            if ([string]::IsNullOrWhiteSpace($batch)) { continue }
            $command = $connection.CreateCommand()
            $command.CommandText = $batch
            $command.CommandTimeout = 120
            [void]$command.ExecuteNonQuery()
        }
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

if ($Server -like '(localdb)*') {
    Write-Host "Starting LocalDB..."
    sqllocaldb start ($Server -replace '^\(localdb\)\\', '') | Out-Null
}

$master = "Server=$Server;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
$target = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"

# DB_ID returns DBNull (which PowerShell treats as true) when the database is missing.
$databaseId = Get-Scalar $master "SELECT DB_ID(N'$Database')"
if ($null -eq $databaseId -or $databaseId -is [System.DBNull]) {
    Write-Host "Creating database $Database..."
    Invoke-Sql $master "CREATE DATABASE [$Database]"
}

$tables = Get-Scalar $target "SELECT COUNT(*) FROM sys.tables WHERE name <> '__EFMigrationsHistory'"
if ($tables -eq 0) {
    $schemaFile = Join-Path $root 'App_Data\dev-schema.sql'
    New-Item -ItemType Directory -Force (Split-Path $schemaFile) | Out-Null

    Write-Host "Generating schema from the EF model (dotnet ef dbcontext script)..."
    Push-Location $root
    try {
        dotnet ef dbcontext script `
            --project src/StreetBiz.Infrastructure `
            --startup-project src/StreetBiz.API `
            --context StreetBizDbContext `
            --output $schemaFile
        if ($LASTEXITCODE -ne 0) { throw "dotnet ef dbcontext script failed. Install it with: dotnet tool install --global dotnet-ef" }
    } finally {
        Pop-Location
    }

    Write-Host "Applying schema..."
    Invoke-Sql $target (Get-Content $schemaFile -Raw)
} else {
    Write-Host "$Database already has $tables table(s); schema left untouched."
}

Write-Host "Seeding roles and wards..."
Invoke-Sql $target (Get-Content (Join-Path $root 'docs\dev-seed.sql') -Raw -Encoding UTF8)

$wards = Get-Scalar $target "SELECT COUNT(*) FROM AdministrativeUnits WHERE unit_type = 'WARD'"
Write-Host "Done. Wards available: $wards"
