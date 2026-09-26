@echo off
echo Building LiveWall from the source folder...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0source\build.ps1" -OutDir "%~dp0."
if errorlevel 1 (
  echo.
  echo Build failed - see the messages above.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
