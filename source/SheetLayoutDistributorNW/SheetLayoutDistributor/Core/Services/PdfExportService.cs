using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using SheetLayoutDistributor.Core.Interfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace SheetLayoutDistributor.Core.Services
{
    /// <summary>
    /// Экспорт подготовленных листов (Layout) в ОДИН многостраничный PDF
    /// низкоуровневым PlotEngine (PublishEngine). Движок использует
    /// НАСТРОЕННЫЕ на листе PC3 (DWG To PDF...) и CTB (monochrome.ctb),
    /// поэтому выход — векторный, кадрированный по рамке и монохромный.
    /// </summary>
    public class PdfExportService
    {
        public const string LayoutPrefix = "Лист_";

        /// <summary>Собрать листы-раскладки (Лист_N) в порядке возрастания N.</summary>
        public List<(string Name, ObjectId Id)> GetSheets(ICommandContext ctx)
        {
            var named = new List<(string name, ObjectId id)>();
            using Transaction tr = ctx.Db.TransactionManager.StartTransaction();
            var dict = (DBDictionary)tr.GetObject(ctx.Db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DictionaryEntry de in dict)
            {
                string nm = (string)de.Key;
                if (!nm.StartsWith(LayoutPrefix, StringComparison.Ordinal)) continue;
                named.Add((nm, (ObjectId)de.Value));
            }
            tr.Commit();
            return named.OrderBy(x => SheetNumber(x.name)).Select(x => (x.name, x.id)).ToList();
        }

        /// <summary>Экспортировать все листы-раскладки в один PDF.</summary>
        /// <returns>Путь к созданному PDF или null.</returns>
        public string ExportSheetsToPdf(ICommandContext ctx, string pdfPath)
        {
            List<(string Name, ObjectId Id)> sheets = GetSheets(ctx);
            if (sheets.Count == 0)
            {
                ctx.LogWarn("Нет листов (Лист_*) для экспорта.");
                return null;
            }

            if (File.Exists(pdfPath)) { try { File.Delete(pdfPath); } catch { } }
            ctx.LogInfo($"Экспорт в PDF: {sheets.Count} л., файл '{pdfPath}'");

            // Принудительно навешиваем монохромный стиль на каждый лист ПЕРЕД печатью.
            // Без этого листы печатаются цвета объектов, а Ghostscript переводит
            // цвет в серый по яркости (зелёный/красный → светло-серый). monochrome.ctb
            // сводит все цвета к 100% чёрному ещё на этапе движка печати.
            ForceMonochromeStyles(ctx, sheets);

            var validator = new PlotInfoValidator
            {
                MediaMatchingPolicy = MatchingPolicy.MatchEnabled
            };
            int printed = 0;

            // КАЖДЫЙ лист → отдельный PDF-файл (часть), затем склейка Ghostscript'ом в pdfPath.
            // Причина: движок «DWG To PDF» в ОДНОМ BeginDocument фиксирует формат страницы по
            // первой странице, поэтому смешанные носители (A2 594x420 среди широких 841x297)
            // отваливаются с eInvalidPlotInfo — отсюда исходный баг «3 листа -> 2 стр.».
            // Ghostscript штатно объединяет страницы разного размера в один PDF.
            string partsDir = Path.Combine(
                Path.GetDirectoryName(pdfPath) ?? Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(pdfPath) + ".parts");
            var partPaths = new List<string>();
            try
            {
                if (Directory.Exists(partsDir)) Directory.Delete(partsDir, true);
                Directory.CreateDirectory(partsDir);
            }
            catch (Exception dex) { ctx.LogWarn($"Не создана папка частей ('{partsDir}'): {dex.Message}"); }

            using (ctx.Doc.LockDocument())
            {
                string originalLayout = ctx.Lm.CurrentLayout;
                string docName = string.IsNullOrEmpty(ctx.Db.Filename) ? ctx.Doc.Name : ctx.Db.Filename;
                var engine = PlotFactory.CreatePublishEngine();
                bool plotStarted = false;

                try
                {
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        (string name, ObjectId id) = sheets[i];

                        // Приводим PageSetup листа к валидному состоянию ПЕРЕД печатью.
                        // Делаем лист ТЕКУЩИМ до нормализации: PlotType.Window /
                        // SetPlotWindowArea валидны только для текущего layout (иначе eInvalidInput).
                        try { ctx.Lm.CurrentLayout = name; } catch { }
                        TryNormalizeSheet(ctx, id);

                        PlotInfo pi;
                        try
                        {
                            // Валидация PlotInfo требует, чтобы лист был ТЕКУЩИМ (eLayoutNotCurrent).
                            ctx.Lm.CurrentLayout = name;
                            pi = new PlotInfo { Layout = id };
                            validator.Validate(pi);
                        }
                        catch (Exception vex)
                        {
                            ctx.LogError($"Validate() не прошёл для '{name}': {vex.Message}");
                            continue;
                        }

                        // Файл-часть с ASCII-именем (GS-32 иногда спотыкается о кириллицу в имени файла).
                        string partPath = Path.Combine(partsDir, $"p{i:D3}.pdf");
                        bool pageOpen = false, docOpen = false, ok = false;
                        for (int attempt = 0; attempt < 2 && !ok; attempt++)
                        {
                            try
                            {
                                if (!plotStarted) { engine.BeginPlot(null, null); plotStarted = true; }
                                ctx.Lm.CurrentLayout = name;   // печать тоже требует текущий лист
                                NeutralizeDefaultViewport(ctx, id, name); // #1 воссоздаётся при активации
                                // Один документ = один лист = одна страница; свой файл-часть.
                                engine.BeginDocument(pi, docName, null, 1, true, partPath);
                                docOpen = true;
                                engine.BeginPage(new PlotPageInfo(), pi, true, null);
                                pageOpen = true;
                                engine.BeginGenerateGraphics(null);
                                engine.EndGenerateGraphics(null);
                                engine.EndPage(null);
                                pageOpen = false;
                                engine.EndDocument(null);
                                docOpen = false;
                                if (File.Exists(partPath)) { partPaths.Add(partPath); printed++; ok = true; }
                                else { ctx.LogError($"  '{name}': файл-часть не создан."); }
                            }
                            catch (Exception pex)
                            {
                                // Аккуратно закрываем начатые, но не завершённые сущности,
                                // чтобы не повредить уже дописанные страницы и состояние движка.
                                if (pageOpen) { try { engine.EndPage(null); } catch { } pageOpen = false; }
                                if (docOpen) { try { engine.EndDocument(null); } catch { } docOpen = false; }

                                bool invalid = pex.Message != null && pex.Message.Contains("eInvalidPlotInfo");
                                if (attempt == 0 && invalid && SetFitRetry(ctx, id))
                                {
                                    ctx.LogInfo($"  '{name}': повтор — Extents+fit...");
                                    pi = new PlotInfo { Layout = id };
                                    try { validator.Validate(pi); } catch { }
                                    continue;
                                }
                                ctx.LogError($"Лист '{name}' пропущен (сбой печати): {pex.GetType().Name}: {pex.Message}");
                                ctx.LogDebug($"  '{name}' stack: {FirstLines(pex.ToString(), 4)}");
                            }
                        }
                    }

                    if (plotStarted) { try { engine.EndPlot(null); } catch { } }
                }
                catch (Exception ex)
                {
                    ctx.LogError("Исключение PlotEngine", ex);
                    try { engine.EndPlot(null); } catch { }
                    try { Directory.Delete(partsDir, true); } catch { }
                    return null;
                }
                finally
                {
                    try { engine.Destroy(); } catch { }
                    try { ctx.Lm.CurrentLayout = originalLayout; } catch { }
                }
            }

            // Склейка частей в итоговый PDF выполняется ТОЛЬКО если отпечатаны ВСЕ листы.
            // Если хоть один лист пропущен (eInvalidPlotInfo, сбой Validate() и т.п.) — документ
            // неполный: не склеиваем, удаляем части и возвращаем null. Тогда dwg2pdf.bat не
            // найдёт итоговый PDF и выйдет с ненулевым кодом, задача PDM не опубликует PDF,
            // не выставит pdf=1 и НЕ переведёт исходник по рабочему процессу (состояние не меняется).
            bool created = false;
            if (printed == sheets.Count && partPaths.Count == sheets.Count)
                created = MergePartsToPdf(ctx, partPaths, pdfPath);

            try { if (Directory.Exists(partsDir)) Directory.Delete(partsDir, true); } catch { }
            if (!created)
            {
                try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { }
            }
            ctx.LogInfo(created
                ? $"PDF создан: {pdfPath} ({new FileInfo(pdfPath).Length} байт, {printed} стр.)"
                : $"PDF НЕ принят: отпечатано {printed} из {sheets.Count} л. — неполный документ удалён, состояние исходника не меняется.");
            return created ? pdfPath : null;
        }

        /// <summary>Открыть лист на запись и нормализовать его PageSetup под печать в PDF.</summary>
        private bool TryNormalizeSheet(ICommandContext ctx, ObjectId layoutId, bool preferStandard = false)
        {
            try
            {
                using Transaction tr = ctx.Db.TransactionManager.StartTransaction();
                if (!(tr.GetObject(layoutId, OpenMode.ForWrite) is Layout layout)) return false;
                bool ok = new PrinterConfigure().NormalizeForPdf(layout, ctx, preferStandard);
                tr.Commit();
                return ok;
            }
            catch (Exception ex)
            {
                ctx.LogWarn($"Нормализация листа не удалась: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// При активации листа AutoCAD воссоздаёт дефолтный viewport №1 с унаследованным
        /// (на смещённых листах — раздутым до 742×420) видом; его прямоугольник превышает
        /// формат и делает PlotInfo невалидным (eInvalidPlotInfo), даже при PlotType=Window.
        /// Нейтрализуем №1 в момент печати: удаляем (разрешено, есть рамочный VP#2), а если
        /// удалить нельзя (активный vport) — принудительно приводим №1 к прямоугольнику и виду
        /// рамочного VP#2, чтобы extents листа совпали с форматом.
        /// </summary>
        private void NeutralizeDefaultViewport(ICommandContext ctx, ObjectId layoutId, string name)
        {
            try
            {
                using Transaction tr = ctx.Db.TransactionManager.StartTransaction();
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

                Viewport frame = null, def = null;
                foreach (ObjectId id in btr)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport v) || v.IsErased) continue;
                    if (v.Number == 1) def = (Viewport)tr.GetObject(id, OpenMode.ForRead);
                    else if (v.Width > 1 && v.Height > 1) frame = (Viewport)tr.GetObject(id, OpenMode.ForRead);
                }
                if (def == null) { tr.Commit(); return; } // №1 уже удалён — ничего делать не нужно

                if (frame != null)
                {
                    // Пробуем удалить №1 (когда активен — бросит eActiveVport, уйдём в fallback).
                    try
                    {
                        def.UpgradeOpen();
                        def.Erase();
                        ctx.LogDebug($"  '{name}': дефолтный VP#1 удалён перед печатью.");
                        tr.Commit();
                        return;
                    }
                    catch { }

                    // Fallback: делаем №1 копией рамки (прямоугольник + вид + масштаб).
                    try
                    {
                        def.UpgradeOpen();
                        def.CenterPoint = frame.CenterPoint;
                        def.Width = frame.Width;
                        def.Height = frame.Height;
                        def.ViewCenter = frame.ViewCenter;
                        def.ViewTarget = frame.ViewTarget;
                        def.ViewDirection = frame.ViewDirection;
                        def.CustomScale = frame.CustomScale;
                        def.TwistAngle = frame.TwistAngle;
                        ctx.LogDebug($"  '{name}': VP#1 приведён к рамке ({frame.Width:F0}×{frame.Height:F0}).");
                    }
                    catch (Exception sex) { ctx.LogDebug($"  '{name}': VP#1 не скорректирован: {sex.Message}"); }
                }
                tr.Commit();
            }
            catch (Exception ex) { ctx.LogDebug($"  '{name}': NeutralizeDefaultViewport: {ex.Message}"); }
        }

        /// <summary>
        /// Повтор под гипотезу о полях печати: окно/экстент = вся рамка (594×420) при 1:1
        /// может не влезать в печатаемую область pc3‑носителя (unprintable margins) —
        /// AutoCAD отклоняет BeginPage (eInvalidPlotInfo). Переходим на Extents + ScaleToFit
        /// + центрирование: рамка гарантированно вписывается в печать, лист остаётся своим
        /// форматом, масштаб чуть меньше 1:1 на величину полей.
        /// </summary>
        private bool SetFitRetry(ICommandContext ctx, ObjectId layoutId)
        {
            try
            {
                using Transaction tr = ctx.Db.TransactionManager.StartTransaction();
                if (!(tr.GetObject(layoutId, OpenMode.ForWrite) is Layout layout)) { tr.Commit(); return false; }
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotType(layout, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                psv.SetUseStandardScale(layout, true);
                psv.SetStdScaleType(layout, StdScaleType.ScaleToFit);
                psv.SetPlotCentered(layout, true);
                tr.Commit();
                return true;
            }
            catch (Exception ex) { ctx.LogDebug($"  SetFitRetry: {ex.Message}"); return false; }
        }

        /// <summary>
        /// Склеить PDF-части (каждая — один лист) в один многостраничный PDF через Ghostscript.
        /// Ghostscript штатно объединяет страницы РАЗНОГО размера, что невозможно штатным
        /// PlotEngine в одном BeginDocument (фиксация формата по первой странице).
        /// </summary>
        private bool MergePartsToPdf(ICommandContext ctx, List<string> partPaths, string pdfPath)
        {
            try
            {
                if (partPaths.Count == 0) return false;

                // Одна страница — склейка не нужна, просто переносим часть в итог.
                if (partPaths.Count == 1)
                {
                    File.Copy(partPaths[0], pdfPath, true);
                    return File.Exists(pdfPath);
                }

                string gs = FindGhostscript(ctx);
                if (gs == null)
                {
                    ctx.LogError("Ghostscript не найден — невозможна склейка разноразмерных листов в один PDF.");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = gs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(partPaths[0]) ?? Path.GetTempPath()
                };
                // -dAutoRotatePages=/None — сохранять альбомную/книжную ориентацию каждого листа как есть.
                psi.Arguments =
                    "-dNOPAUSE -dBATCH -sDEVICE=pdfwrite -dAutoRotatePages=/None " +
                    "-dCompatibilityLevel=1.5 -dPDFSETTINGS=/prepress " +
                    $"-sOutputFile=\"{pdfPath}\" " +
                    string.Join(" ", partPaths.Select(p => $"\"{p}\""));

                ctx.LogDebug($"  склейка {partPaths.Count} ч. → {Path.GetFileName(gs)}");
                using (Process pr = Process.Start(psi))
                {
                    string so = pr.StandardOutput.ReadToEnd();
                    string se = pr.StandardError.ReadToEnd();
                    if (!pr.WaitForExit(180000))
                    {
                        try { pr.Kill(); } catch { }
                        ctx.LogError("  склейка PDF: таймаут Ghostscript.");
                        return false;
                    }
                    if (!File.Exists(pdfPath))
                    {
                        ctx.LogError($"  склейка PDF не удалась (код {pr.ExitCode}): {FirstLines(se, 3)}");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                ctx.LogError("  Ошибка склейки PDF", ex);
                return false;
            }
        }

        /// <summary>Найти gswinXXc.exe: env DWG2PDF_GS → типовые пути → PATH.</summary>
        private static string FindGhostscript(ICommandContext ctx)
        {
            string env = Environment.GetEnvironmentVariable("DWG2PDF_GS");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            string[] known =
            {
                @"C:\Program Files\PDFCreator\Ghostscript\bin\gswin64c.exe",
                @"C:\Program Files\PDFCreator\Ghostscript\bin\gswin32c.exe",
            };
            foreach (string k in known) if (File.Exists(k)) return k;

            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (string nm in new[] { "gswin64c.exe", "gswin32c.exe" })
                {
                    try { string f = Path.Combine(dir.Trim(), nm); if (File.Exists(f)) return f; }
                    catch { }
                }
            }
            return null;
        }

        private static string FirstLines(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string[] ls = s.Replace("\r", "").Split('\n');
            return string.Join(" | ", ls.Take(n));
        }

        private static int SheetNumber(string layoutName)
        {
            string tail = layoutName.Length > LayoutPrefix.Length
                ? layoutName.Substring(LayoutPrefix.Length) : "0";
            return int.TryParse(tail, out int n) ? n : int.MaxValue;
        }

        /// <summary>
        /// Принудительно устанавливает монохромный стиль печати (monochrome.ctb)
        /// на всех листах перед экспортом. Если чертёж находится в режиме именных
        /// стилей (STB) и .ctb применить нельзя, пишет предупреждение — в этом
        /// случае обесцвечивание полностью ложится на Ghostscript.
        /// </summary>
        private void ForceMonochromeStyles(ICommandContext ctx, List<(string Name, ObjectId Id)> sheets)
        {
            int ok = 0, fail = 0;
            var psv = PlotSettingsValidator.Current;
            using Transaction tr = ctx.Db.TransactionManager.StartTransaction();
            foreach ((string name, ObjectId id) in sheets)
            {
                try
                {
                    var layout = (Layout)tr.GetObject(id, OpenMode.ForWrite);
                    psv.SetCurrentStyleSheet(layout, "monochrome.ctb");
                    layout.PlotPlotStyles = true;
                    layout.ScaleLineweights = true;
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    ctx.LogDebug($"  monochrome.ctb не применён к '{name}': {ex.Message}");
                }
            }
            tr.Commit();
            ctx.LogInfo($"  Монохром monochrome.ctb: применён к {ok} л." +
                        (fail > 0 ? $", ошибок {fail}" : "") + ".");
        }
    }
}

