@echo off
setlocal
if not "%~1"=="" goto RUN_UNINSTALL

set "APPROOT=%~dp0"
set "TEMPBASE=%TEMP%\AFMM-Uninstall-%RANDOM%-%RANDOM%"
mkdir "%TEMPBASE%" >nul 2>nul
if errorlevel 1 exit /b 1
copy "%~f0" "%TEMPBASE%\Uninstall.bat" >nul
if errorlevel 1 exit /b 1
start "" /D "%TEMPBASE%" cmd.exe /c ""%TEMPBASE%\Uninstall.bat" "%APPROOT%" "%TEMPBASE%""
exit /b 0

:RUN_UNINSTALL
set "APPROOT=%~1"
set "TEMPBASE=%~2"
copy "%APPROOT%Uninstall.ps1" "%TEMPBASE%\Uninstall.ps1" >nul
if errorlevel 1 (
  echo Could not stage the uninstaller.
  pause
  exit /b 1
)
cd /d "%TEMPBASE%"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%TEMPBASE%\Uninstall.ps1" -AppRoot "%APPROOT%"
set "RC=%ERRORLEVEL%"
echo.
pause
cd /d "%TEMP%"
rmdir /s /q "%TEMPBASE%" >nul 2>nul
exit /b %RC%
