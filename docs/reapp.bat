@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul

rem Revision 4: immediate start; clean every vault directory under Plugins.
if not defined LOCALAPPDATA (
 echo ОШИБКА: переменная LOCALAPPDATA не определена.
 pause
 exit /b 1
)
if not exist "%LOCALAPPDATA%\" (
 echo ОШИБКА: каталог LOCALAPPDATA недоступен.
 pause
 exit /b 1
)

echo.
echo Завершение процессов...
taskkill /F /IM ConisioAdmin.exe
taskkill /F /IM ItemExplorer.exe
taskkill /F /IM AddInSrv.exe
taskkill /F /IM EdmServer.exe
taskkill /F /IM explorer.exe

echo.
echo Очистка файлов и вложенных папок Plugins...
set "REAPP_ERRORS=0"
set "REAPP_PLUGINS=%LOCALAPPDATA%\SOLIDWORKS\SOLIDWORKS PDM\Plugins"
if exist "%REAPP_PLUGINS%\" (
 for /f "eol=| delims=" %%V in ('dir /b /a:d "%REAPP_PLUGINS%\*" 2^>nul') do (
  set "REAPP_VAULT=%%V"
  call :CleanCurrentVault
 )
) else (
 echo Каталог Plugins отсутствует: "%REAPP_PLUGINS%"
)

echo.
echo Запуск Проводника...
start "" "%SystemRoot%\explorer.exe"
echo.
if "%REAPP_ERRORS%"=="0" (
 echo Очистка завершена. Проверьте вход в PDM.
) else (
 echo ВНИМАНИЕ: не все файлы удалось удалить. Смотрите сообщения выше.
 echo Возможно, DLL заняты другим процессом или нет прав доступа.
 pause
)
exit /b %REAPP_ERRORS%

:CleanCurrentVault
set "REAPP_TARGET=%REAPP_PLUGINS%\%REAPP_VAULT%"
if not exist "%REAPP_TARGET%\" (
 echo Каталог отсутствует: "%REAPP_TARGET%"
 exit /b 0
)
echo Обработка: "%REAPP_TARGET%"
del /s /q /f /a "%REAPP_TARGET%\*"
rem Remove immediate child directories, including hidden ones, recursively.
rem Preserve the vault cache root itself.
for /f "eol=| delims=" %%D in ('dir /b /a:d "%REAPP_TARGET%\*" 2^>nul') do (
 rmdir /s /q "%REAPP_TARGET%\%%D"
)
rem Check all remaining immediate children: both files and directories.
for /f "eol=| delims=" %%F in ('dir /b /a "%REAPP_TARGET%\*" 2^>nul') do (
 echo Не удалено: "%REAPP_TARGET%\%%F"
 set "REAPP_ERRORS=1"
)
exit /b 0
