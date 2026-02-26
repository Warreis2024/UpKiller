Write-Host "Update iceren process, service ve scheduled task'ler taraniyor..." -ForegroundColor Cyan

$killedProcesses = @()
$stoppedServices = @()
$disabledTasks = @()

# 1️⃣ Processleri kontrol et
Get-Process | Where-Object { $_.ProcessName -match "update" } | ForEach-Object {
    try {
        Stop-Process -Id $_.Id -Force
        $killedProcesses += $_.ProcessName
    } catch {}
}

# 2️⃣ Servisleri kontrol et
Get-Service | Where-Object { $_.Name -match "update" -or $_.DisplayName -match "update" } | ForEach-Object {
    try {
        Stop-Service $_.Name -Force
        Set-Service $_.Name -StartupType Disabled
        $stoppedServices += $_.Name
    } catch {}
}

# 3️⃣ Scheduled Task'leri kontrol et
Get-ScheduledTask | Where-Object { $_.TaskName -match "update" } | ForEach-Object {
    try {
        Disable-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath
        $disabledTasks += $_.TaskName
    } catch {}
}

# 📢 Rapor
Write-Host "`n---- RAPOR ----" -ForegroundColor Yellow

if ($killedProcesses.Count -gt 0) {
    Write-Host "Kill edilen processler:"
    $killedProcesses | ForEach-Object { Write-Host " - $_" }
} else {
    Write-Host "Kill edilen process yok."
}

if ($stoppedServices.Count -gt 0) {
    Write-Host "`nDurdurulan ve disable edilen servisler:"
    $stoppedServices | ForEach-Object { Write-Host " - $_" }
} else {
    Write-Host "Durdurulan servis yok."
}

if ($disabledTasks.Count -gt 0) {
    Write-Host "`nDisable edilen scheduled taskler:"
    $disabledTasks | ForEach-Object { Write-Host " - $_" }
} else {
    Write-Host "Disable edilen scheduled task yok."
}

Write-Host "`nIslem tamamlandi." -ForegroundColor Green