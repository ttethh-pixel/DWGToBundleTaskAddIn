@echo off
setlocal enabledelayedexpansion
rem ================= ПУТИ: аргумент > переменная окружения > по умолчанию =================
rem  1-й аргумент: DWG (обязателен)
rem  2-й аргумент: accoreconsole.exe        (или env DWG2PDF_ACCORECONSOLE)
rem  3-й аргумент: gswin32c/gswin64c.exe     (или env DWG2PDF_GS)
rem  env DWG2PDF_DLL - SheetLayoutDistributor.dll (NETLOAD-плагин)
set "AC_DEFAULT=D:\Program Files\AutoCAD\AutoCAD 2020\accoreconsole.exe"
set "GS_DEFAULT=C:\Program Files\PDFCreator\Ghostscript\bin\gswin32c.exe"
set "DLL_DEFAULT=%~dp0SheetLayoutDistributor\bin\Release\net48\SheetLayoutDistributor.dll"
set "AC=%~2"
if "%AC%"=="" if not "%DWG2PDF_ACCORECONSOLE%"=="" set "AC=%DWG2PDF_ACCORECONSOLE%"
if "%AC%"=="" set "AC=%AC_DEFAULT%"
set "GS=%~3"
if "%GS%"=="" if not "%DWG2PDF_GS%"=="" set "GS=%DWG2PDF_GS%"
if "%GS%"=="" set "GS=%GS_DEFAULT%"
set "DLL=%DWG2PDF_DLL%"
if "%DLL%"=="" set "DLL=%DLL_DEFAULT%"
if "%~1"=="" (echo Usage: dwg2pdf.bat "C:\path\file.dwg" [accoreconsole.exe] [gswinXXc.exe] & exit /b 1)
set "DWG=%~f1"
if not exist "%DWG%" (echo DWG not found: %DWG% & exit /b 1)
if not exist "%AC%" (echo accoreconsole not found: %AC% & exit /b 1)
if not exist "%GS%" (echo gswin not found: %GS% & exit /b 1)
for %%F in ("%DWG%") do (set "DIR=%%~dpF" & set "BASE=%%~nF")
set "COLOR=%DIR%%BASE%.pdf"
set "MONO=%DIR%%BASE%новый.pdf"
set "SCR=%DIR%%BASE%.run.tmp.scr"
> "%SCR%" echo SECURELOAD
>>"%SCR%" echo 0
>>"%SCR%" echo FILEDIA
>>"%SCR%" echo 0
>>"%SCR%" echo CMDDIA
>>"%SCR%" echo 0
>>"%SCR%" echo NETLOAD
>>"%SCR%" echo %DLL%
>>"%SCR%" echo DWG_TO_PDF
>>"%SCR%" echo QUIT
>>"%SCR%" echo Y
set "SLD_HEADLESS=1"
del "%COLOR%" "%MONO%" 2>nul
echo Step 1/2: AutoCAD -^> color PDF ^(crop to frame^)...
"%AC%" /i "%DWG%" /s "%SCR%" /l en-US
del "%SCR%" 2>nul
if not exist "%COLOR%" (echo Color PDF failed. Check plugin logs\export_log.txt & exit /b 1)
echo Step 2/2: Ghostscript -^> mono ^(DeviceGray + blacken^)...
rem settransfer: near-white stays white, everything else -> 100%% black.
rem Fixes "gray" output on STB drawings where monochrome.ctb can't be applied.
rem OutputFile set BEFORE -c because -o after -c breaks arg parsing in gs 9.52.
"%GS%" -dNOPAUSE -dBATCH -sDEVICE=pdfwrite -dProcessColorModel=/DeviceGray -dColorConversionStrategy=/Gray -dOverrideICC=true -sOutputFile="%MONO%" -c "{ 0.98 ge { 1.0 } { 0.0 } ifelse } bind settransfer" -f "%COLOR%"
del "%COLOR%" 2>nul
if exist "%MONO%" (echo Done: %MONO%) else (echo Mono PDF failed. & exit /b 1)
endlocal
