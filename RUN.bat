@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
if not exist "LATEST_EXE.txt" (
  call BUILD_WINDOWS.bat
  if errorlevel 1 exit /b 1
)
set /p EXENAME=<LATEST_EXE.txt
if not exist "!EXENAME!" (
  echo Build artifact !EXENAME! is missing.
  exit /b 1
)
start "" "%~dp0!EXENAME!"
exit /b 0
