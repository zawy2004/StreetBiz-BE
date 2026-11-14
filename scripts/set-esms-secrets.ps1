<#
.SYNOPSIS
  Stores the eSMS credentials in this machine's user-secrets, so the LOCAL API sends real OTP SMS.

.DESCRIPTION
  The keys are typed at a hidden prompt and written to user-secrets (outside the repository),
  never to appsettings or any file under git. The API picks them up in Development: with
  Sms:Provider=Esms it sends through eSMS, without it OTP codes are printed to the API console.

  Start with -Sandbox: eSMS then only validates the request (nothing is sent or charged), which
  proves the keys work. Run again without -Sandbox to send a real message.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/set-esms-secrets.ps1 -Sandbox

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/set-esms-secrets.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/set-esms-secrets.ps1 -Clear
#>
param(
    [switch]$Sandbox,
    # Back to printing OTP codes on the API console.
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\StreetBiz.API'

function Set-Secret([string]$name, [string]$value) {
    dotnet user-secrets --project $project set $name $value | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set $name" }
}

function Read-Secret([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if ($Clear) {
    foreach ($key in 'Provider', 'ApiKey', 'SecretKey', 'Sandbox') {
        dotnet user-secrets --project $project remove "Sms:$key" | Out-Null
    }
    Write-Host 'eSMS secrets removed: OTP codes print on the API console again.'
    return
}

$apiKey = Read-Secret 'eSMS Api Key'
$secretKey = Read-Secret 'eSMS Secret Key'
if (-not $apiKey -or -not $secretKey) { throw 'Both keys are required.' }

Set-Secret 'Sms:Provider' 'Esms'
Set-Secret 'Sms:ApiKey' $apiKey
Set-Secret 'Sms:SecretKey' $secretKey
Set-Secret 'Sms:Sandbox' ($(if ($Sandbox) { 'true' } else { 'false' }))

if ($Sandbox) {
    Write-Host 'Saved (SANDBOX: eSMS validates the request, nothing is sent). Run again without -Sandbox to send for real.'
} else {
    Write-Host 'Saved (REAL SMS: each OTP request is sent and charged).'
}
Write-Host 'Restart the API, then register with your own phone number.'
