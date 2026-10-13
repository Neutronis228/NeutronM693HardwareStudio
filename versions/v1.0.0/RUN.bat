@echo off
cd /d "%~dp0"
if not exist "NeutronM693SmoothClicker.exe" (
  echo EXE not found. Run BUILD_WINDOWS.bat first.
  pause
  exit /b 1
)
start "" "%CD%\NeutronM693SmoothClicker.exe"
