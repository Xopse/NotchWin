# NotchWin installer: builds the app and installs it like a normal Windows app.
#   - installs to   %LocalAppData%\Programs\NotchWin
#   - adds Start menu entries "NotchWin" and "NotchWin Settings"
#   - starts automatically with Windows
#   - shows up in Settings > Apps so you can uninstall it
# Run it by double-clicking "Install NotchWin.bat". Running it again updates the app (your settings are kept).
# Works both from the source code (builds it, needs the .NET 10 SDK) and from a GitHub release zip (ready-made).

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Programs\NotchWin'
$exe  = Join-Path $dest 'NotchWin.exe'

function Fail($msg) {
    Write-Host ""
    Write-Host $msg -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

Write-Host ""
Write-Host "  NotchWin installer" -ForegroundColor Cyan
Write-Host "  ------------------" -ForegroundColor Cyan

# Downloaded from GitHub Releases? Then the app is already built (in the "app" folder) and no .NET is needed.
$prebuilt = Join-Path $here 'app'
$usePrebuilt = Test-Path (Join-Path $prebuilt 'NotchWin.exe')

# 1. Close the running copy (if any) so its files can be replaced
Write-Host "[1/5] Closing NotchWin if it's running..."
Get-Process NotchWin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# 2. Get the app: ready-made, or build it from the source code
if ($usePrebuilt) {
    Write-Host "[2/5] Using the ready-made build..."
    $build = $prebuilt
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail "This is the source code version, which needs the .NET 10 SDK to build (https://dotnet.microsoft.com/download). Or download the ready-made zip from the project's GitHub Releases page instead."
    }
    Write-Host "[2/5] Building (takes a minute the first time)..."
    $build = Join-Path $env:TEMP 'NotchWin-build'
    if (Test-Path $build) { Remove-Item $build -Recurse -Force }
    & dotnet publish (Join-Path $here 'NotchWin.csproj') -c Release -o $build --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Fail "Build failed. Copy the errors above and send them over." }
}

# 3. Copy into the install folder
Write-Host "[3/5] Installing to $dest ..."
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Get-ChildItem $dest -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $build '*') $dest -Recurse -Force
Copy-Item (Join-Path $here 'uninstall.ps1') $dest -Force
if (-not $usePrebuilt) { Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue }
# Files from a downloaded zip are marked 'from the internet'; clear that so Windows doesn't block them.
Get-ChildItem $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# 4. Start menu + desktop shortcuts
Write-Host "[4/5] Adding Start menu and desktop shortcuts..."
$shell = New-Object -ComObject WScript.Shell
$programs = [Environment]::GetFolderPath('Programs')

$lnk = $shell.CreateShortcut((Join-Path $programs 'NotchWin.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.Description = 'NotchWin - a notch for Windows'
$lnk.Save()

$lnk = $shell.CreateShortcut((Join-Path $programs 'NotchWin Settings.lnk'))
$lnk.TargetPath = $exe
$lnk.Arguments = '--settings'
$lnk.WorkingDirectory = $dest
$lnk.Description = 'NotchWin settings'
$lnk.Save()

$desktop = [Environment]::GetFolderPath('Desktop')
$lnk = $shell.CreateShortcut((Join-Path $desktop 'NotchWin.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.Description = 'Start NotchWin (opens Settings if it is already running)'
$lnk.Save()

# 5. Start with Windows + "Installed apps" entry
Write-Host "[5/5] Registering with Windows..."
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NotchWin' -Value "`"$exe`""

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchWin'
New-Item -Path $key -Force | Out-Null
$sizeKb = [int]((Get-ChildItem $dest -Recurse | Measure-Object -Property Length -Sum).Sum / 1KB)
Set-ItemProperty $key -Name DisplayName     -Value 'NotchWin'
Set-ItemProperty $key -Name DisplayVersion  -Value '1.6.0'
Set-ItemProperty $key -Name Publisher       -Value 'Me'
Set-ItemProperty $key -Name DisplayIcon     -Value $exe
Set-ItemProperty $key -Name InstallLocation -Value $dest
Set-ItemProperty $key -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
Set-ItemProperty $key -Name NoModify        -Value 1 -Type DWord
Set-ItemProperty $key -Name NoRepair        -Value 1 -Type DWord
Set-ItemProperty $key -Name EstimatedSize   -Value $sizeKb -Type DWord

# Done: start it
Start-Process $exe
Write-Host ""
Write-Host "  Done! NotchWin is running at the top of your screen." -ForegroundColor Green
Write-Host "  Open 'NotchWin Settings' from the Start menu (or right-click the notch) to customise it."
Write-Host "  You can delete the download folder now."
Write-Host ""
Read-Host "Press Enter to close"
