# Pull ONCESI — DLL kilidini acar
$ErrorActionPreference = "Continue"

$SiteRoot = "C:\Plesk Vhosts\ledajans.com\geldim.ledajans.com"
if ($PSScriptRoot -and (Test-Path (Join-Path (Split-Path -Parent $PSScriptRoot) "Ledajans.Server.dll"))) {
    $SiteRoot = Split-Path -Parent $PSScriptRoot
}

$PoolName = "geldim.ledajans.com(domain)(4.0)(pool)"
$AppCmd = "$env:windir\system32\inetsrv\appcmd.exe"

Write-Host "Site: $SiteRoot" -ForegroundColor Cyan

$persistScript = Join-Path $PSScriptRoot "plesk-persist-webconfig.ps1"
if (Test-Path $persistScript) {
    & $persistScript -SiteRoot $SiteRoot
}

$html = "<!DOCTYPE html><html><head><meta charset=`"utf-8`"><title>Guncelleniyor</title></head><body><p>Ledajans guncelleniyor, lutfen bekleyin...</p></body></html>"
Set-Content -Path (Join-Path $SiteRoot "app_offline.htm") -Value $html -Encoding UTF8
Write-Host "app_offline.htm olusturuldu." -ForegroundColor Green

if (Test-Path $AppCmd) {
    & $AppCmd stop apppool "/apppool.name:$PoolName" 2>$null
    Write-Host "App pool durduruldu: $PoolName" -ForegroundColor Green
    Start-Sleep -Seconds 10
}

Write-Host "Simdi Plesk > Pull Now" -ForegroundColor Cyan
