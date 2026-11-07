@echo off
rem Offline build of DWGToBundleTaskAddIn.dll via csc.exe (.NET Framework 4.x).
rem Uses a response file so we can list recursive .cs sources. Runs fine under
rem PowerShell AllSigned (cmd batch is not subject to PS execution policy).
setlocal
set "ROOT=%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
set "EPDM=C:\Program Files\SOLIDWORKS PDM\EPDM.Interop.epdm.dll"
set "OUT=%ROOT%bin\DWGToBundleTaskAddIn.dll"
set "RSP=%TEMP%\dwg2pdm_build.rsp"

if not exist "%ROOT%bin" mkdir "%ROOT%bin"
if not exist "%ROOT%Resources\dwg2pdf.bat" (echo [ERROR] missing Resources\dwg2pdf.bat & exit /b 2)
if not exist "%ROOT%Resources\SheetLayoutDistributor.dll" (echo [ERROR] missing Resources\SheetLayoutDistributor.dll & exit /b 2)

>  "%RSP%" echo /nologo /target:library /platform:anycpu /optimize+ /codepage:65001
>> "%RSP%" echo /out:"%OUT%"
>> "%RSP%" echo /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Xml.dll"
>> "%RSP%" echo /r:"%EPDM%"
>> "%RSP%" echo /resource:"%ROOT%Resources\dwg2pdf.bat",DWGToBundleTaskAddIn.Resources.dwg2pdf.bat
>> "%RSP%" echo /resource:"%ROOT%Resources\SheetLayoutDistributor.dll",DWGToBundleTaskAddIn.Resources.SheetLayoutDistributor.dll
for %%F in ("%ROOT%*.cs" "%ROOT%Properties\*.cs") do >> "%RSP%" echo "%%~fF"

echo csc ... -^> "%OUT%"
"%CSC%" @"%RSP%"
set "RC=%ERRORLEVEL%"
del "%RSP%" 2>nul
if not "%RC%"=="0" (echo BUILD FAILED rc=%RC% & exit /b %RC%)
for %%F in ("%OUT%") do echo OK: %%~zF bytes
exit /b 0
