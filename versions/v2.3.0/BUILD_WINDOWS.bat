@echo off
setlocal EnableExtensions
cd /d "%~dp0"
chcp 65001 >nul

echo =============================================================
echo  Neutron M693 Hardware Studio 2.3.0 PRO - BUILD
echo =============================================================

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo.
  echo ERROR: .NET Framework C# compiler not found.
  echo Install/enable .NET Framework 4.x and run this file again.
  pause
  exit /b 1
)

if not exist "Assets\appico.ico" (
  echo ERROR: Assets\appico.ico not found.
  pause
  exit /b 1
)

echo Compiler: %CSC%
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"Assets\appico.ico" /out:NeutronM693HardwareStudio.exe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll NeutronM693HardwareStudio.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Copy the entire error text to ChatGPT.
  pause
  exit /b 1
)

echo.
echo BUILD OK:
echo   %CD%\NeutronM693HardwareStudio.exe
echo.
echo IMPORTANT: exit the official Redragon OemDrv.exe from the tray
 echo before connecting, so it does not consume the HID command responses.
echo.
pause
