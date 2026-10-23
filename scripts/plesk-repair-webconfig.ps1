# web.config tamamen yeniden yazar — POST/OPTIONS 405 icin
param(
    [string]$SiteRoot = "C:\Plesk Vhosts\ledajans.com\geldim.ledajans.com",
    [string]$PersistPath = "C:\Ledajans\config\web.config"
)

$ErrorActionPreference = "Stop"

function Repair-WebConfig {
    param([string]$ConfigPath)

    if (-not (Test-Path $ConfigPath)) {
        Write-Host "Atlaniyor (yok): $ConfigPath" -ForegroundColor Yellow
        return
    }

    Copy-Item $ConfigPath "$ConfigPath.bak" -Force

    $envVars = @{}
    [xml]$old = Get-Content $ConfigPath
    foreach ($n in $old.SelectNodes("//environmentVariable")) {
        $envVars[$n.GetAttribute("name")] = $n.GetAttribute("value")
    }
    if (-not $envVars["ASPNETCORE_ENVIRONMENT"]) {
        $envVars["ASPNETCORE_ENVIRONMENT"] = "Production"
    }

    $envXml = ""
    foreach ($kv in ($envVars.GetEnumerator() | Sort-Object Name)) {
        $envXml += "          <environmentVariable name=`"$($kv.Key)`" value=`"$($kv.Value)`" />`r`n"
    }

    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <modules>
        <remove name="WebDAVModule" />
      </modules>
      <handlers>
        <remove name="WebDAV" />
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\Ledajans.Server.dll"
                  stdoutLogEnabled="true"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="OutOfProcess">
        <environmentVariables>
$envXml        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
"@

    Set-Content -Path $ConfigPath -Value $content -Encoding UTF8
    Write-Host "Duzeltildi: $ConfigPath" -ForegroundColor Green
}

New-Item -ItemType Directory -Path (Split-Path $PersistPath -Parent) -Force | Out-Null
Repair-WebConfig -ConfigPath (Join-Path $SiteRoot "web.config")
Repair-WebConfig -ConfigPath $PersistPath

$siteConfig = Join-Path $SiteRoot "web.config"
if (Test-Path $siteConfig) {
    Copy-Item $siteConfig $PersistPath -Force
    Write-Host "Kalici yedek guncellendi: $PersistPath" -ForegroundColor Green
}

$pool = "geldim.ledajans.com(domain)(4.0)(pool)"
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
if (Test-Path $appcmd) {
    & $appcmd recycle apppool "/apppool.name:$pool"
    Write-Host "App pool yenilendi: $pool" -ForegroundColor Green
}

Write-Host "Tamam — site + kalici yedek guncellendi" -ForegroundColor Cyan
