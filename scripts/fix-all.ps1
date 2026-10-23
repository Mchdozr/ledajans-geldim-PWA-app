# web.config + kalici yedek duzelt, siteyi ac
$SiteRoot = "C:\Plesk Vhosts\ledajans.com\geldim.ledajans.com"
$PersistPath = "C:\Ledajans\config\web.config"
$PoolName = "geldim.ledajans.com(domain)(4.0)(pool)"
$AppCmd = "$env:windir\system32\inetsrv\appcmd.exe"

New-Item -ItemType Directory -Path "C:\Ledajans\config" -Force | Out-Null

function Fix-Config([string]$path) {
    if (-not (Test-Path $path)) {
        Write-Host "Atlaniyor: $path" -ForegroundColor Yellow
        return
    }
    Copy-Item $path "$path.bak" -Force
    $envVars = @{}
    [xml]$old = Get-Content $path
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
    $content = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n"
    $content += "<configuration>`r`n"
    $content += "  <location path=`".`" inheritInChildApplications=`"false`">`r`n"
    $content += "    <system.webServer>`r`n"
    $content += "      <modules><remove name=`"WebDAVModule`" /></modules>`r`n"
    $content += "      <handlers><remove name=`"WebDAV`" /><add name=`"aspNetCore`" path=`"*`" verb=`"*`" modules=`"AspNetCoreModuleV2`" resourceType=`"Unspecified`" /></handlers>`r`n"
    $content += "      <aspNetCore processPath=`"dotnet`" arguments=`".\Ledajans.Server.dll`" stdoutLogEnabled=`"true`" stdoutLogFile=`".\logs\stdout`" hostingModel=`"OutOfProcess`">`r`n"
    $content += "        <environmentVariables>`r`n$envXml        </environmentVariables>`r`n"
    $content += "      </aspNetCore>`r`n    </system.webServer>`r`n  </location>`r`n</configuration>`r`n"
    Set-Content -Path $path -Value $content -Encoding UTF8
    Write-Host "OK: $path" -ForegroundColor Green
}

Fix-Config (Join-Path $SiteRoot "web.config")
Fix-Config $PersistPath
Copy-Item (Join-Path $SiteRoot "web.config") $PersistPath -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $SiteRoot "app_offline.htm") -ErrorAction SilentlyContinue
if (Test-Path $AppCmd) {
    & $AppCmd start apppool "/apppool.name:$PoolName" 2>$null
    Start-Sleep -Seconds 2
    & $AppCmd recycle apppool "/apppool.name:$PoolName" 2>$null
}
Write-Host "Tamam" -ForegroundColor Cyan
