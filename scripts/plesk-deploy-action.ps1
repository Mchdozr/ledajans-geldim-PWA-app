# Site kokundeki scripts/ klasorunden calisir (deploy branch ile gelir)
$ErrorActionPreference = "Continue"
$SiteRoot = Split-Path -Parent $PSScriptRoot
$PersistScript = Join-Path $PSScriptRoot "plesk-persist-webconfig.ps1"
$PoolName = "geldim.ledajans.com(domain)(4.0)(pool)"
$AppCmd = "$env:windir\system32\inetsrv\appcmd.exe"

if (Test-Path $PersistScript) {
    & $PersistScript -SiteRoot $SiteRoot
}

$repairScript = Join-Path $PSScriptRoot "plesk-repair-webconfig.ps1"
if (Test-Path $repairScript) {
    & $repairScript -SiteRoot $SiteRoot
}

Remove-Item (Join-Path $SiteRoot "app_offline.htm") -ErrorAction SilentlyContinue

if (Test-Path $AppCmd) {
    & $AppCmd start apppool "/apppool.name:$PoolName" 2>$null
    Start-Sleep -Seconds 2
    & $AppCmd recycle apppool "/apppool.name:$PoolName" 2>$null
    Write-Host "App pool yenilendi: $PoolName"
}

