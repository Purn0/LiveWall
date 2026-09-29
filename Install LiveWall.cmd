@echo off
rem Builds LiveWall from the "source" folder when it is there (a copy of the repository),
rem otherwise installs the LiveWall.exe next to this file (the release download).
if not exist "%~dp0source\build.ps1" goto prebuilt
echo Building LiveWall from the source folder...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0source\build.ps1" -OutDir "%~dp0."
if errorlevel 1 goto buildfailed
goto install

:prebuilt
if exist "%~dp0LiveWall.exe" goto install
echo.
echo LiveWall.exe was not found next to this file. Unzip the whole download first.
echo.
pause
exit /b 1

:buildfailed
echo.
echo BUILD FAILED - nothing was installed. See the compiler messages above.
echo.
pause
exit /b 1

:install
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo.
  echo INSTALL FAILED - see the messages above.
)
echo.
pause
