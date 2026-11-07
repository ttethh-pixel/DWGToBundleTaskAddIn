# AGENTS.md — DWGToBundleTaskAddIn

Проект: COM‑аддин задач SOLIDWORKS PDM (Standard) `DWGToBundleTaskAddIn.dll`
(CLSID `{EFBE62F2-6ED8-4FA7-9975-49F92FBD6215}`), конвертирует DWG/DXF в ч/б PDF
через встроенный движок `dwg2pdf.bat` (accoreconsole + Ghostscript) и возвращает PDF в архив.

## Снятие процессов PDM — re.bat
В корне проекта лежит **`re.bat`** — штатный способ снять процессы PDM, которые держат
залоченную DLL аддина. Содержимое:
- `taskkill /F /IM ItemExplorer.exe`
- `taskkill /F /IM AddInSrv.exe`
- `taskkill /F /IM EdmServer.exe`
- `taskkill /F /IM explorer.exe` затем `start explorer.exe`

Использовать `re.bat`, когда нужно освободить блокировку
`...\AppData\Local\SOLIDWORKS\SOLIDWORKS PDM\Plugins\<Vault>\{CLSID}4\DWGToBundleTaskAddIn.dll`
перед заменой DLL. Побочный эффект: перезапускается `explorer.exe` (моргают рабочий стол и
панель задач). `re.bat` НЕ трогает `ConisioAdmin.exe` / `ViewServer.exe` — их закрывать/
перезапускать отдельно при необходимости.

## Деплой (как реально грузится аддин)
- Аддин зарегистрирован в **HKCU** (per‑user), грузится из папки плагинов хранилища:
  `...\AppData\Local\SOLIDWORKS\SOLIDWORKS PDM\Plugins\ClassificatorTest\{EFBE62F2-…}4\DWGToBundleTaskAddIn.dll`
  рядом лежит `DWGToBundleTaskAddIn.CAF` (контрольная запись EPDM `ConisioAddInFile`).
- Дurable‑замена — через ConisioAdmin: vault → Add‑Ins → Удалить → Добавить с пути
  `D:\progs\DWGToBundleTaskAddIn\publish\DWGToBundleTaskAddIn.dll`, затем перезапуск ViewServer.
- Быстрая проверка на локальной копии: снять блокировку `re.bat` → заменить DLL по пути
  плагинов → перечитать хранилище. При пересинхронизации EPDM может вернуть копию из
  хранилища, поэтому финально — через ConisioAdmin.
- Откат/сборка: `build.cmd` (пишет в `bin\`), артефакт для деплоя — `publish\`. Перед
  заменой делать бэкап текущей DLL рядом (`.bak-<size>-<date>`).

## Известный дефект (не в этом коде)
Пустые PDF (0 страниц) порождает плагин AutoCAD `SheetLayoutDistributor`: для части чертежей
`eInvalidPlotInfo` → битый цветной PDF → Ghostscript `No pages will be processed`. Чинится на
стороне плагина/AutoCAD (PageSetup/plot‑стили/шрифты), не правками возврата в архив.