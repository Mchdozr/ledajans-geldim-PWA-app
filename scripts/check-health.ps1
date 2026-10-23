# Sunucuda migration ve cihaz eşlemesi kontrolu
$healthUrl = "https://geldim.ledajans.com/health"
try {
    $r = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 30
    Write-Host "status: $($r.status)"
    Write-Host "migrationsPending: $($r.migrationsPending)"
    if ($r.PSObject.Properties.Name -contains "deviceBindingEnabled") {
        Write-Host "deviceBindingEnabled: $($r.deviceBindingEnabled)"
    }
    if ($r.migrationsPending -gt 0) {
        Write-Host "Bekleyen migration var. App pool yeniden baslatin." -ForegroundColor Yellow
        exit 1
    }
    if ($r.PSObject.Properties.Name -contains "deviceBindingEnabled" -and -not $r.deviceBindingEnabled) {
        Write-Host "UYARI: Eski surum calisiyor olabilir (deviceBindingEnabled=false)." -ForegroundColor Yellow
    }
    exit 0
} catch {
    Write-Host "Health kontrolu basarisiz: $_" -ForegroundColor Red
    exit 1
}
