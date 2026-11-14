<#
.SYNOPSIS
  Copies the VPS backups (database .bak.gz and uploads .tar.gz) to this machine, so a lost VPS
  does not take its only backups with it.

.DESCRIPTION
  Lists /opt/streetbiz/backups over SSH and downloads only the files missing from -Destination,
  so it is cheap to run every week (or from Task Scheduler). Nothing on the server is changed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/pull-backups.ps1 -VpsHost 103.78.1.179 -KeyPath $HOME\.ssh\streetbiz_deploy
#>
param(
    [Parameter(Mandatory = $true)][string]$VpsHost,
    [Parameter(Mandatory = $true)][string]$KeyPath,
    [string]$User = "deploy",
    [string]$RemoteDir = "/opt/streetbiz/backups",
    [string]$Destination = (Join-Path $env:USERPROFILE "streetbiz-backups")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $KeyPath)) { throw "SSH key not found: $KeyPath" }
New-Item -ItemType Directory -Force $Destination | Out-Null

$target = "$User@$VpsHost"
$remote = ssh -i $KeyPath -o BatchMode=yes $target "ls -1 $RemoteDir/*.gz 2>/dev/null"
if ($LASTEXITCODE -ne 0) { throw "Could not list $RemoteDir on $VpsHost." }

$downloaded = 0
foreach ($path in ($remote | Where-Object { $_ -match '\.gz$' })) {
    $name = Split-Path $path -Leaf
    $local = Join-Path $Destination $name
    if (Test-Path $local) { continue }

    scp -i $KeyPath -o BatchMode=yes -q "${target}:$path" "$local"
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $local -ErrorAction SilentlyContinue
        throw "Download failed: $name"
    }
    $downloaded++
    Write-Host "Downloaded $name"
}

Write-Host "$downloaded new file(s); backups are in $Destination"
