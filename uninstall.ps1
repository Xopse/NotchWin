# Removes NotchWin. Runs from Settings > Apps > NotchWin > Uninstall.

$dest = Join-Path $env:LOCALAPPDATA 'Programs\NotchWin'

Write-Host "Removing NotchWin..." -ForegroundColor Cyan
Get-Process NotchWin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NotchWin' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchWin' -Recurse -Force -ErrorAction SilentlyContinue

$programs = [Environment]::GetFolderPath('Programs')
Remove-Item (Join-Path $programs 'NotchWin.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $programs 'NotchWin Settings.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'NotchWin.lnk') -ErrorAction SilentlyContinue

Set-Location $env:TEMP
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue

$answer = Read-Host "Also delete your NotchWin settings and downloaded voice model? (y/N)"
if ($answer -match '^[yY]') {
    Remove-Item (Join-Path $env:APPDATA 'NotchWin') -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "NotchWin has been removed." -ForegroundColor Green
Start-Sleep -Seconds 2
