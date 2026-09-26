# Installs LiveWall for the current user (no administrator rights needed):
#   %LOCALAPPDATA%\Programs\LiveWall, a Start menu shortcut, and "start when I sign in".
$ErrorActionPreference = 'Stop'
$src  = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LiveWall'
$exe  = Join-Path $dest 'LiveWall.exe'

if (-not (Test-Path (Join-Path $src 'LiveWall.exe'))) { throw "LiveWall.exe was not found in $src. Build it first (Install LiveWall.cmd does)." }

# Stop the running copy (and its player processes) so the exe can be replaced: ask politely, then force.
function Get-Installed { @(Get-Process LiveWall -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) }
if (Test-Path $exe) {
    if ((Get-Installed).Count -gt 0) { Start-Process -FilePath $exe -ArgumentList '--exit' -Wait }
    for ($i = 0; $i -lt 50 -and (Get-Installed).Count -gt 0; $i++) { Start-Sleep -Milliseconds 100 }
}
$left = Get-Installed
if ($left.Count -gt 0) {
    $left | Stop-Process -Force
    $left | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}
if ((Get-Installed).Count -gt 0) { throw 'LiveWall is still running and could not be stopped. Close it from the tray icon and try again.' }

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src 'LiveWall.exe'), (Join-Path $src 'LiveWall.exe.config'), (Join-Path $src 'uninstall.ps1'), (Join-Path $src 'README.txt') $dest -Force
if ((Get-FileHash $exe).Hash -ne (Get-FileHash (Join-Path $src 'LiveWall.exe')).Hash) { throw "The new LiveWall.exe could not be copied to $dest." }
Write-Host ("Installed LiveWall.exe built " + (Get-Item $exe).LastWriteTime)

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'LiveWall.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = 'LiveWall - live wallpapers'
$link.Save()

# Extra Start menu entries (right-click > Pin to taskbar / Pin to Start to get one-click buttons).
foreach ($s in @(
    @{ Name = 'LiveWall Board'; Args = '--daily-board'; Desc = "Show today's board and draw on it" },
    @{ Name = 'LiveWall Draw';  Args = '--draw';        Desc = 'Draw on the wallpaper (or the board)' })) {
    $l = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) ($s.Name + '.lnk')))
    $l.TargetPath = $exe
    $l.Arguments = $s.Args
    $l.WorkingDirectory = $dest
    $l.Description = $s.Desc
    $l.Save()
}

Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LiveWall' -Value "`"$exe`" --autostart"

Start-Process -FilePath $exe
Write-Host ""
Write-Host "LiveWall is installed in $dest and will start when you sign in."
Write-Host "Its Settings window opens now: add your videos, GIFs or pictures there."
Write-Host "Later: click the LiveWall icon in the notification area, or start LiveWall from the Start menu."
