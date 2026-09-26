# Installs LiveWall for the current user (no administrator rights needed):
#   %LOCALAPPDATA%\Programs\LiveWall, a Start menu shortcut, and "start when I sign in".
$ErrorActionPreference = 'Stop'
$src  = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LiveWall'
$exe  = Join-Path $dest 'LiveWall.exe'

if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList '--exit' -Wait
    Start-Sleep -Seconds 1
}
Get-Process LiveWall -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src 'LiveWall.exe'), (Join-Path $src 'LiveWall.exe.config'), (Join-Path $src 'uninstall.ps1'), (Join-Path $src 'README.txt') $dest -Force

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
