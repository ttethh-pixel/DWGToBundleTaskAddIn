@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM  DWGToBundleTaskAddIn 1.0.2.0 - registration WITHOUT regasm.exe
REM  (regasm is an SDK tool and may be absent; reg.exe is always there).
REM  Writes the SAME HKLM keys that "regasm /codebase" would write,
REM  so the PDM Agent/Card service (other account) can open the CLSID.
REM  RUN AS ADMINISTRATOR. Run this ON THE MACHINE WHERE PDM AGENT RUNS.
REM ============================================================

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo [ERROR] Nuzhny prava Administratora - pishem v HKLM.
  echo         Pravoj klick po failu ^> "Run as administrator".
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
set "CACHE=%LOCALAPPDATA%\SOLIDWORKS\SOLIDWORKS PDM\Plugins\ClassificatorTest\%CLSID%6"

if not exist "%SRC%" ( echo [ERROR] net novogo DLL: "%SRC%" & exit /b 1 )

REM --- .NET Framework 4.x proverka po REESTRU (ne po failu!) ---
set "NET4="
"%R%" query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"   /v Release >nul 2>&1 && set "NET4=1"
if not defined NET4 "%R%" query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Client" /v Release >nul 2>&1 && set "NET4=1"
if not defined NET4 ( echo [ERROR] .NET Framework 4.x ne niden. Ustanovite .NET Framework 4.8 x64 i povtorite. & exit /b 1 )

REM --- shim mscoree.dll: shtatochno v System32 (v papke Framework64 ego NET) ---
if not exist "%MSCOREE%" set "MSCOREE=mscoree.dll"

REM CodeBase in the exact file:/// form regasm uses:
set "CODEURL=file:///%DEST:\=/%"

echo [1/5] Udalenie STAROJ per-user HKCU-registracii i kesh 1.0.0.0...
"%R%" delete "HKCU\Software\Classes\CLSID\%CLSID%" /f >nul 2>&1
"%R%" delete "HKLM\Software\Classes\CLSID\%CLSID%" /f >nul 2>&1
if exist "%CACHE%" rmdir /s /q "%CACHE%" >nul 2>&1

echo [2/5] Kopirovanie novogo 1.0.2.0 DLL v postoyannoe mesto...
if not exist "%DESTDIR%" mkdir "%DESTDIR%"
copy /y "%SRC%" "%DEST%" >nul
if errorlevel 1 ( echo [ERROR] ne udalos' skopirovat' v "%DEST%" & exit /b 1 )

echo [3/5] Rjadom EPDM.Interop.epdm.dll (net v GAC - nuzhen regasm/runtime)...
if exist "%EPDM%" copy /y "%EPDM%" "%DESTDIR%\" >nul

echo [4/5] Zapis' klyuchej HKLM (analog regasm /codebase)...
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%"                          /ve /d "%NSCLASS%"        /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32"           /ve /d "%MSCOREE%"        /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32"           /v ThreadingModel /t REG_SZ /d Both /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32"           /v Class           /t REG_SZ /d "%NSCLASS%" /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32"           /v Assembly        /t REG_SZ /d "%ASM%"     /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32"           /v RuntimeVersion  /t REG_SZ /d "%RTV%"     /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32"           /v CodeBase        /t REG_SZ /d "%CODEURL%" /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\.NETAssembly"             /v RuntimeVersion  /t REG_SZ /d "%RTV%"     /f >nul || goto :fail
"%R%" add   "HKLM\Software\Classes\CLSID\%CLSID%\.NETAssembly"             /v CodeBase        /t REG_SZ /d "%CODEURL%" /f >nul || goto :fail

echo [5/5] Proverka HKLM...
"%R%" query "HKLM\Software\Classes\CLSID\%CLSID%\InprocServer32" /v CodeBase
if errorlevel 1 goto :fail

echo.
echo GOTOVO - CLSID teper v HKLM (viden sluzhbe PDM pod lyubym akkauntom).
echo   CodeBase = "%CODEURL%"
echo.
echo Dalshe OBYAZATEL'NO:
echo   1) Zakroyte formu zadachi / perezapustite AddInSrv (zadachi idut v vashey sessii).
echo   2) V PDM Administration: Vault ^> Add-ins ^> udalite STARUyu zapis' i Add zanovo,
echo      ukhav "%DEST%". (Inogda dostatochno prosto ponovno zapustit' zadachu - CLSID
echo      razreshaetsya cherez HKLM CodeBase i gruzhitsya 1.0.2.0.)
echo   3) Versiya adicina dolzhna byt' 10002 (1.0.2.0).
echo   4) Dajte akkauntu sluzhby PDM (esli on drugoy) CHTENIE na "%DESTDIR%".
echo   5) Esli agent na DRUGOY mashine - povtorite etot skript tam zhe.
echo.
pause
exit /b 0

:fail
echo [ERROR] Registraciya v HKLM ne udalas' (sm. soobshcheniya vyше).
pause
exit /b 1
