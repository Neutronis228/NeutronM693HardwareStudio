@echo off
setlocal EnableExtensions
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
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /codepage:65001 %ICON% /out:NeutronM693HardwareStudio.exe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll src\NeutronM693HardwareStudio.cs
if errorlevel 1 (
  echo BUILD FAILED.
  exit /b 1
)
echo BUILD OK.
echo Test: NeutronM693HardwareStudio.exe --smoke-test
echo Crash log: %%LOCALAPPDATA%%\NeutronM693HardwareStudio\STARTUP_ERROR.log
exit /b 0
