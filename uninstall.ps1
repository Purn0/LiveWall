# Removes LiveWall and puts back the Windows wallpaper you had before installing it.
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LiveWall'
$exe  = Join-Path $dest 'LiveWall.exe'

if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList '--exit' -Wait
    Start-Sleep -Seconds 2
    Get-Process LiveWall -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Start-Process -FilePath $exe -ArgumentList '--restore-wallpaper' -Wait
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LiveWall' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'LiveWall' -ErrorAction SilentlyContinue
foreach ($n in 'LiveWall.lnk', 'LiveWall Board.lnk', 'LiveWall Draw.lnk') {
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) $n) -Force -ErrorAction SilentlyContinue
}

$answer = Read-Host "Also delete your LiveWall settings, boards and drawings, and converted-video cache? (y/N)"
if ($answer -match '^[yY]') {
    Remove-Item (Join-Path $env:APPDATA 'LiveWall') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'LiveWall') -Recurse -Force -ErrorAction SilentlyContinue
}
# The program folder may contain this very script; remove it last, from a separate process.
Start-Process -FilePath 'cmd.exe' -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
Write-Host "LiveWall has been removed."
