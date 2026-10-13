@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo =============================================================
echo  Neutron M693 Smooth Clicker 1.0.0 - Windows build
echo =============================================================
echo.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo ERROR: Windows .NET Framework C# compiler not found.
  echo Enable/install .NET Framework 4.8, then run this file again.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
 /out:NeutronM693SmoothClicker.exe NeutronM693SmoothClicker.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)

echo.
echo BUILD OK: %CD%\NeutronM693SmoothClicker.exe
echo.
set /p RUNNOW=Run now? [Y/N]: 
if /I "%RUNNOW%"=="Y" start "" "%CD%\NeutronM693SmoothClicker.exe"
endlocal
