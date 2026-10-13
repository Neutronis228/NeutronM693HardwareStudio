@echo off
cd /d "%~dp0"
if not exist NeutronM693HardwareStudio.exe (
  echo EXE not found. Run BUILD_WINDOWS.bat first.
  pause
  exit /b 1
)
start "" NeutronM693HardwareStudio.exe
