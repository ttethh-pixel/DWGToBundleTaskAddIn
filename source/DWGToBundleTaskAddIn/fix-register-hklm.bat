@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM  DWGToBundleTaskAddIn - COMPLETE HKLM COM registration.
REM  Fixes "CoCreateInstance 0x80070002 / ne udalos' otkryt' reestr"
REM  caused by a missing VERSION subkey (InprocServer32\1.0.0.0)
REM  that mscoree/Fusion requires for .NET COM activation.
REM  Writes the exact tree that "regasm /codebase" produces.
REM  RUN AS ADMINISTRATOR. Run ON THE MACHINE WHERE PDM/ConisioAdmin RUNS.
REM ============================================================

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo [ERROR] Nuzhny prava Administratora - pishem v HKLM.
  echo         Pravoj klick po failu ^> "Run as administrator".
  echo.
  pause
  exit /b 1
)

set "R=%WINDIR%\System32\reg.exe"
set "SRC=D:\progs\DWGToBundleTaskAddIn-1.0.2.0\DWGToBundleTaskAddIn.dll"
set "DESTDIR=C:\PDM\DWGToBundleTaskAddIn"
set "DEST=%DESTDIR%\DWGToBundleTaskAddIn.dll"
set "EPDM=C:\Program Files\SOLIDWORKS PDM\EPDM.Interop.epdm.dll"
set "MSCOREE=%WINDIR%\System32\mscoree.dll"
set "CLSID={EFBE62F2-6ED8-4FA7-9975-49F92FBD6215}"
set "NSCLASS=DWGToBundleTaskAddIn.DwgBundleAddIn"
set "ASM=DWGToBundleTaskAddIn, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
set "RTV=v4.0.30319"
set "VER=1.0.0.0"
set "BASE=HKLM\Software\Classes\CLSID\%CLSID%"

if not exist "%DEST%" (
  if not exist "%SRC%" ( echo [ERROR] net DLL: "%SRC%" i "%DEST%" & echo. & pause & exit /b 1 )
  if not exist "%DESTDIR%" mkdir "%DESTDIR%"
  copy /y "%SRC%" "%DEST%" >nul
)
if exist "%EPDM%" copy /y "%EPDM%" "%DESTDIR%\" >nul
set "CODEURL=file:///%DEST:\=/%"

echo [1/4] Udalenie STARYH zapisej HKCU i HKLM (chistyj list)...
"%R%" delete "HKCU\Software\Classes\CLSID\%CLSID%" /f >nul 2>&1
"%R%" delete "%BASE%" /f >nul 2>&1
taskkill /F /IM AddInSrv.exe >nul 2>&1

echo [2/4] Polnoe derevo HKLM (kak regasm /codebase), s VERSION-subkey...
"%R%" add   "%BASE%"                              /ve /d "%NSCLASS%" /f >nul || goto :fail
"%R%" add   "%BASE%\ProgId"                       /ve /d "%NSCLASS%" /f >nul || goto :fail
"%R%" add   "%BASE%\Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}" /f >nul 2>&1
REM --- InprocServer32 (parent) ---
"%R%" add   "%BASE%\InprocServer32"               /ve /d "%MSCOREE%" /f >nul || goto :fail
"%R%" add   "%BASE%\InprocServer32"               /v ThreadingModel /t REG_SZ /d Both /f >nul || goto :fail
REM --- InprocServer32\1.0.0.0  (TO i schital mscoree!) ---
"%R%" add   "%BASE%\InprocServer32\%VER%"         /ve /d "%MSCOREE%" /f >nul || goto :fail
"%R%" add   "%BASE%\InprocServer32\%VER%"         /v Assembly       /t REG_SZ /d "%ASM%"     /f || goto :fail
"%R%" add   "%BASE%\InprocServer32\%VER%"         /v Class          /t REG_SZ /d "%NSCLASS%" /f || goto :fail
"%R%" add   "%BASE%\InprocServer32\%VER%"         /v RuntimeVersion /t REG_SZ /d "%RTV%"     /f || goto :fail
"%R%" add   "%BASE%\InprocServer32\%VER%"         /v CodeBase       /t REG_SZ /d "%CODEURL%" /f || goto :fail
REM --- .NETAssembly (+ version) ---
"%R%" add   "%BASE%\.NETAssembly\%VER%"           /ve /d "%MSCOREE%" /f >nul || goto :fail
"%R%" add   "%BASE%\.NETAssembly\%VER%"           /v RuntimeVersion /t REG_SZ /d "%RTV%"     /f || goto :fail
"%R%" add   "%BASE%\.NETAssembly\%VER%"           /v CodeBase       /t REG_SZ /d "%CODEURL%" /f || goto :fail

echo [3/4] Proverka VERSION-subkey...
"%R%" query "%BASE%\InprocServer32\%VER%" /v CodeBase || goto :fail

echo [4/4] GOTOVO. Polnoe derevo s version-subkey zapisano v HKLM.
echo   CodeBase = "%CODEURL%"
echo.
echo  Dalshe:
echo   1) V ConisioAdmin: Vault ^> Add-ins ^> udalit' staruju zapis' i Add zanovo
echo      (ukhav "%DEST%") - teper 'ne udalos' otkryt' reestr' byt' NE DOLZHNO.
echo   2) Peresozdat' zadachu (AddInSrv perezapushchen etim skriptom).
echo   3) V loge %APPDATA%\DWGToBundleTaskAddIn\DWGToBundleTaskAddIn.log dolzhna
echo      poyavit'sya stroka "PDF proveryon" (dlya pustogo - "ne dopushchen ... pdf=0").
echo.
pause
exit /b 0

:fail
echo [ERROR] Zapis' v HKLM ne udalas' (sm. soobshcheniya vyse / net admin-prav?).
echo.
pause
exit /b 1
