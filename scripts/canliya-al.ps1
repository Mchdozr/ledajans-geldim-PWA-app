# Tek komut canli deploy — STANDART YONTEM (her deploy bu script ile)
# Detay: docs/CANLIYA-AL.md

$ErrorActionPreference = "Stop"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$SiteRoot = "C:\Plesk Vhosts\ledajans.com\geldim.ledajans.com"
$PersistConfig = "C:\Ledajans\config\web.config"
$PoolName = "geldim.ledajans.com(domain)(4.0)(pool)"
$AppCmd = "$env:windir\system32\inetsrv\appcmd.exe"
$ZipUrls = @(
    "https://codeload.github.com/Mchdozr/ledajans-geldim/zip/refs/heads/deploy",
    "https://github.com/Mchdozr/ledajans-geldim/archive/refs/heads/deploy.zip"
)
$TempZip = "$env:TEMP\geldim-deploy.zip"
$TempExtract = "$env:TEMP\geldim-deploy-extract"
$OfflineFile = Join-Path $SiteRoot "app_offline.htm"
$siteConfig = Join-Path $SiteRoot "web.config"
$deployOk = $false

function Start-AppPool {
    Remove-Item $OfflineFile -ErrorAction SilentlyContinue
    if (Test-Path $AppCmd) {
        & $AppCmd start apppool "/apppool.name:$PoolName" 2>$null
        Start-Sleep -Seconds 2
        & $AppCmd recycle apppool "/apppool.name:$PoolName" 2>$null
    }
}

function Stop-AppPool {
    $html = "<!DOCTYPE html><html><body><p>Guncelleniyor...</p></body></html>"
    Set-Content -Path $OfflineFile -Value $html -Encoding UTF8
    if (Test-Path $AppCmd) {
        & $AppCmd stop apppool "/apppool.name:$PoolName" 2>$null
    }
    Start-Sleep -Seconds 8
}

function Download-DeployZip {
    param([string[]]$Urls, [string]$OutFile)

    if (Test-Path $OutFile) { Remove-Item $OutFile -Force -ErrorAction SilentlyContinue }

    $delays = @(4, 8, 16, 32)
    $lastError = $null

    foreach ($url in $Urls) {
        for ($i = 0; $i -lt $delays.Count; $i++) {
            try {
                Write-Host "  Indiriliyor ($($i + 1)/$($delays.Count)): $url" -ForegroundColor DarkGray
                Invoke-WebRequest -Uri $url -OutFile $OutFile -UseBasicParsing -TimeoutSec 300 `
                    -Headers @{ "User-Agent" = "Ledajans-Deploy/1.0" }

                if ((Test-Path $OutFile) -and (Get-Item $OutFile).Length -gt 100000) {
                    Write-Host "  Indirme tamam ($([math]::Round((Get-Item $OutFile).Length / 1MB, 1)) MB)" -ForegroundColor DarkGray
                    return
                }

                throw "Zip dosyasi cok kucuk veya bos."
            }
            catch {
                $lastError = $_
                Write-Host "  Hata: $($_.Exception.Message)" -ForegroundColor Yellow
                if ($i -lt ($delays.Count - 1)) {
                    $wait = $delays[$i]
                    Write-Host "  ${wait}s bekleniyor..." -ForegroundColor Yellow
                    Start-Sleep -Seconds $wait
                }
            }
        }
    }

    throw "Deploy zip indirilemedi. Son hata: $lastError"
}

Write-Host "=== Ledajans canliya al ===" -ForegroundColor Cyan

try {
    Stop-AppPool
    Write-Host "[1/5] App pool durduruldu" -ForegroundColor Green

    if (Test-Path $siteConfig) {
        New-Item -ItemType Directory -Path (Split-Path $PersistConfig -Parent) -Force | Out-Null
        Copy-Item $siteConfig $PersistConfig -Force
    }

    Write-Host "[2/5] Deploy indiriliyor..." -ForegroundColor Yellow
    Download-DeployZip -Urls $ZipUrls -OutFile $TempZip

    if (Test-Path $TempExtract) { Remove-Item $TempExtract -Recurse -Force }
    Expand-Archive -Path $TempZip -DestinationPath $TempExtract -Force
    $sourceDir = Get-ChildItem $TempExtract -Directory | Select-Object -First 1
    if (-not $sourceDir) { throw "Zip bos" }

    Write-Host "[3/5] Dosyalar kopyalaniyor..." -ForegroundColor Yellow
    Get-ChildItem $SiteRoot -Force | Where-Object { $_.Name -notin @("web.config", "logs") } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path "$($sourceDir.FullName)\*" -Destination $SiteRoot -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $SiteRoot "logs") -Force | Out-Null

    if (Test-Path $PersistConfig) {
        Copy-Item $PersistConfig $siteConfig -Force
        Write-Host "[4/5] web.config geri yuklendi" -ForegroundColor Green
    }
    else {
        Write-Host "[4/5] UYARI: web.config yedegi yok!" -ForegroundColor Red
    }

    Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
    Remove-Item $TempExtract -Recurse -Force -ErrorAction SilentlyContinue

    Start-AppPool
    Write-Host "[5/5] App pool baslatildi" -ForegroundColor Green

    $versionFile = Join-Path $SiteRoot "wwwroot\version.txt"
    if (Test-Path $versionFile) {
        $ver = (Get-Content $versionFile -Raw).Trim()
        Write-Host "Surum: $ver" -ForegroundColor Cyan
    }

    $deployOk = $true
    Write-Host "Tamam -> https://geldim.ledajans.com" -ForegroundColor Green
}
catch {
    Write-Host "HATA: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Site geri aciliyor (onceki surum)..." -ForegroundColor Yellow
    Start-AppPool
    exit 1
}

if (-not $deployOk) { exit 1 }
