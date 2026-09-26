@echo off
echo Building LiveWall from the source folder...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0source\build.ps1" -OutDir "%~dp0."
if errorlevel 1 (
  echo.
  echo BUILD FAILED - nothing was installed. See the compiler messages above.
  echo.
  pause
  exit /b 1
)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo.
  echo INSTALL FAILED - see the messages above.
)
echo.
pause
