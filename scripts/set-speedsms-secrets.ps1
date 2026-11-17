<#
.SYNOPSIS
  Stores the SpeedSMS access token in this machine's user-secrets, so the LOCAL API sends real OTP SMS.

.DESCRIPTION
  The token is typed at a hidden prompt and written to user-secrets (outside the repository),
  never to appsettings or any file under git. The API picks it up in Development: with
  Sms:Provider=SpeedSms it sends through SpeedSMS, without it OTP codes are printed to the API console.

  Get the token at https://connect.speedsms.vn -> "Tai khoan" (account). SpeedSMS has no sandbox:
  every OTP request is sent and charged, so top up a small balance first.

  -SmsType: 2 customer care / random number (default, no registration), 3 brandname (needs -Sender),
  4 default brandname Notify, 5 own Android phone (needs -Sender = deviceId), 6 fixed number.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/set-speedsms-secrets.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/set-speedsms-secrets.ps1 -SmsType 3 -Sender StreetBiz

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/set-speedsms-secrets.ps1 -Clear
#>
param(
    [ValidateSet('2', '3', '4', '5', '6')][string]$SmsType = '2',
    [string]$Sender = '',
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
    foreach ($key in 'Provider', 'ApiKey', 'SmsType', 'Sender') {
        dotnet user-secrets --project $project remove "Sms:$key" | Out-Null
    }
    Write-Host 'SpeedSMS secrets removed: OTP codes print on the API console again.'
    return
}

if ($SmsType -in '3', '5' -and -not $Sender) { throw "-SmsType $SmsType needs -Sender (brandname or Android deviceId)." }

$token = Read-Secret 'SpeedSMS access token'
if (-not $token) { throw 'The access token is required.' }

Set-Secret 'Sms:Provider' 'SpeedSms'
Set-Secret 'Sms:ApiKey' $token
Set-Secret 'Sms:SmsType' $SmsType
if ($Sender) { Set-Secret 'Sms:Sender' $Sender }

Write-Host "Saved (REAL SMS, type $SmsType): each OTP request is sent and charged."
Write-Host 'Restart the API, then register with your own phone number.'
