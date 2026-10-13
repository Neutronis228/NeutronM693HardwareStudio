@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo =============================================================
echo  Neutron M693 Direct HID 1.1.0 - Windows build
echo =============================================================
echo.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo ERROR: .NET Framework C# compiler not found.
  echo Enable/install .NET Framework 4.8 and try again.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
 /out:NeutronM693DirectHID.exe NeutronM693DirectHID.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)

echo.
echo BUILD OK: %CD%\NeutronM693DirectHID.exe
echo.
set /p RUNNOW=Run now? [Y/N]: 
if /I "%RUNNOW%"=="Y" start "" "%CD%\NeutronM693DirectHID.exe"
endlocal
