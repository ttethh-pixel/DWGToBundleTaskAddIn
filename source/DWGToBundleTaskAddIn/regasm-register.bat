@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM  DWGToBundleTaskAddIn - register via the REAL RegAsm.exe.
REM  RegAsm writes the complete COM tree incl. the VERSION subkey
REM  (InprocServer32\1.0.0.0) that mscoree/Fusion needs. This is what
REM  fixes "CoCreateInstance 0x80070002" / ConisioAdmin
REM  "ne udalos' otkryt' reestr Windows".
REM  RUN AS ADMINISTRATOR. Run where PDM / ConisioAdmin run.
REM ============================================================

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo [ERROR] Nuzhny prava Administratora - pishem v HKLM.
  echo         Pravoj klick po failu ^> "Run as administrator". & echo. & pause & exit /b 1
)

set "R=%WINDIR%\System32\reg.exe"
set "DLL=C:\PDM\DWGToBundleTaskAddIn\DWGToBundleTaskAddIn.dll"
set "CLSID={EFBE62F2-6ED8-4FA7-9975-49F92FBD6215}"
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

if not exist "%REGASM%" ( echo [ERROR] RegAsm ne najden: "%REGASM%" & echo. & pause & exit /b 1 )
if not exist "%DLL%"    ( echo [ERROR] DLL ne najdena: "%DLL%"       & echo. & pause & exit /b 1 )

echo [1/4] Snyatie blokirovki PDM i chistyj unreg...
taskkill /F /IM AddInSrv.exe    >nul 2>&1
taskkill /F /IM ItemExplorer.exe >nul 2>&1
"%REGASM%" /unregister "%DLL%" >nul 2>&1
"%R%" delete "HKCU\Software\Classes\CLSID\%CLSID%" /f >nul 2>&1
"%R%" delete "HKLM\Software\Classes\CLSID\%CLSID%" /f >nul 2>&1

echo [2/4] RegAsm /codebase (polnoe derevo v HKLM, version-subkey)...
"%REGASM%" /codebase "%DLL%"
if errorlevel 1 ( echo [ERROR] RegAsm vershnul oshibku. & echo. & pause & exit /b 1 )

echo [3/4] Proverka version-subkey...
"%R%" query "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32\1.0.0.0" /v CodeBase || (
  echo [ERROR] version-subkey ne sozdan - RegAsm ne zapisal polnuyu strukturu. & echo. & pause & exit /b 1 )

echo [4/4] GOTOVO. RegAsm zapisal korrektnuyu COM-registratsiyu.
echo.
echo  Dalshe:
echo   - V ConisioAdmin oshibki 'ne udalos' otkryt' reestr' byt' NE DOLZHNO.
echo     Pri neobhodimosti: Vault ^> Add-ins ^> Add, ukhav "%DLL%".
echo   - Peresozdat' zadachu (AddInSrv perezapushchen). V loge
echo     %%APPDATA%%\DWGToBundleTaskAddIn\DWGToBundleTaskAddIn.log dolzhna byt'
echo     stroka "PDF proveryon" / dlya pustogo PDF - "ne dopushchen ... pdf=0".
echo.
pause
exit /b 0
