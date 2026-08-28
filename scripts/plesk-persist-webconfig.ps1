# web.config kalici yedek — deploy klasoru disinda tutulur, git pull silmez
param(
    [string]$SiteRoot = "C:\Plesk Vhosts\ledajans.com\geldim.ledajans.com",
    [string]$PersistPath = "C:\Ledajans\config\web.config"
)

$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Path (Split-Path $PersistPath -Parent) -Force | Out-Null

$configPath = Join-Path $SiteRoot "web.config"
$backupPath = Join-Path $SiteRoot "web.config.backup"

if (Test-Path $PersistPath) {
    Copy-Item $PersistPath $configPath -Force
    Copy-Item $PersistPath $backupPath -Force
    Write-Host "web.config geri yuklendi: $PersistPath -> $configPath"
}

elseif (Test-Path $backupPath) {
    Copy-Item $backupPath $configPath -Force
    Copy-Item $backupPath $PersistPath -Force
    Write-Host "web.config site yedeginden geri yuklendi."
}

elseif (Test-Path $configPath) {
    Copy-Item $configPath $PersistPath -Force
    Copy-Item $configPath $backupPath -Force
    Write-Host "web.config yedeklendi: $PersistPath"
}

else {
    Write-Host "web.config bulunamadi. Elle olusturun." -ForegroundColor Yellow
    exit 1
}

$repairScript = Join-Path $PSScriptRoot "plesk-repair-webconfig.ps1"
if (Test-Path $repairScript) {
    & $repairScript -SiteRoot $SiteRoot -PersistPath $PersistPath
}
