@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if not exist "NeutronM693HardwareStudio.exe" (
  call BUILD_WINDOWS.bat
  if errorlevel 1 exit /b 1
)
echo Running no-HID UI smoke test...
"NeutronM693HardwareStudio.exe" --smoke-test
if errorlevel 1 (
  echo.
  echo Startup failed. Diagnostics:
  if exist "%LOCALAPPDATA%\NeutronM693HardwareStudio\STARTUP_ERROR.log" type "%LOCALAPPDATA%\NeutronM693HardwareStudio\STARTUP_ERROR.log"
  exit /b 1
)
echo UI started and exited cleanly.
exit /b 0
