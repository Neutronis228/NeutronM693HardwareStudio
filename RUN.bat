@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if not exist "NeutronM693HardwareStudio.exe" (
  call BUILD_WINDOWS.bat
  if errorlevel 1 exit /b 1
)
start "" "%~dp0NeutronM693HardwareStudio.exe"
exit /b 0
