@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
chcp 65001 >nul
echo === Neutron M693 Hardware Studio - Windows build ===
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework compiler csc.exe was not found.
  exit /b 1
)
set "ICON="
if exist "Assets\appico.ico" set "ICON=/win32icon:Assets\appico.ico"
for /f %%D in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss_fff"') do set "BUILDSTAMP=%%D"
set "EXENAME=NeutronM693HardwareStudio_!BUILDSTAMP!.exe"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /codepage:65001 %ICON% /out:!EXENAME! /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll src\NeutronM693HardwareStudio.cs
if errorlevel 1 (
  echo BUILD FAILED.
  exit /b 1
)
echo !EXENAME!> LATEST_EXE.txt
copy /Y "!EXENAME!" "NeutronM693HardwareStudio.exe" >nul 2>nul
if errorlevel 1 echo NOTE: Previous EXE is locked; your new build is !EXENAME!.
echo BUILD OK: !EXENAME!.
echo Test: !EXENAME! --smoke-test
echo Crash log: %%LOCALAPPDATA%%\NeutronM693HardwareStudio\STARTUP_ERROR.log
exit /b 0
