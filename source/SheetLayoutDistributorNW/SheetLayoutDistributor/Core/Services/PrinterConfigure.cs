using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetLayoutDistributor.Core.Interfaces;
using SheetLayoutDistributor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SheetLayoutDistributor.Core.Services
{
    public class PrinterConfigure
    {
        // ЧИТАЕМЫЙ текст (кириллица): основным делаем стковой DWG To PDF.pc3 (движок
        // PDF 2020 корректно встраивает шрифты — Ghostscript/вьюверы не ломают кириллицу).
        // Расширенный all_formats (шаблон 2016) пишет CID-подшрифт, который gs 9.52 не
        // декодирует (/Indentity0.Unicode /CIDDecoding), из-за чего русский становится
        // нечитаемым — поэтому он оставлен только запасным (для машин, где нет сткового).
        private const string PrinterPrimary = "DWG To PDF.pc3";
        private const string PrinterFallback = "DWG To PDF_all_formats.pc3";

        public (string printer, string mediaName) FindFormat(
            FrameInfo frame, ICommandContext ctx)
        {
            var psv = PlotSettingsValidator.Current;
            string printer = ResolvePrinter(ctx);
            if (printer == null)
            {
                ctx.LogError($"Принтеры не найдены ('{PrinterPrimary}' / '{PrinterFallback}')!");
                return (null, null);
            }

            ctx.LogInfo($"  Принтер: {printer}");

            // ВАЖНО: PlotSettings(true) = paper space — только так GetCanonicalMediaNameList
            // возвращает данные в AutoCAD 2020 [12]
            using var tmp = new PlotSettings(true);
            psv.SetPlotPaperUnits(tmp, PlotPaperUnit.Millimeters);

            var mediaList = GetMediaNames(psv, tmp, printer, ctx);
            if (mediaList.Count == 0)
            {
                ctx.LogError("  Список форматов пуст!");
                return (printer, null);
            }

            ctx.LogDebug($"  Форматов доступно: {mediaList.Count}");

            // Поиск точного совпадения (±1 мм)
            foreach (string media in mediaList)
            {
                try
                {
                    psv.SetPlotConfigurationName(tmp, printer, media);
                    psv.RefreshLists(tmp);

                    Point2d size = tmp.PlotPaperSize; // свойство PlotSettings [13]

                    if (Math.Abs(size.Y - frame.Width) < 1.0 && Math.Abs(size.X - frame.Height) < 1.0)
                    {
                        frame.NeedsSheetRotation = true;
                        ctx.LogInfo($"  Совпадение: '{media}' ({size.X:F1}×{size.Y:F1})");
                        return (printer, media);
                    }
                    if (Math.Abs(size.X - frame.Width) < 1.0 && Math.Abs(size.Y - frame.Height) < 1.0)
                    {
                        frame.NeedsSheetRotation = false;
                        ctx.LogInfo($"  Совпадение: '{media}' ({size.X:F1}×{size.Y:F1})");
                        return (printer, media);
                    }
                }
                catch { }
            }

            // Ближайший по площади
            string best = FindClosest(psv, tmp, printer, frame.Width, frame.Height, mediaList, ctx);
            ctx.LogWarn($"  Нет точного {frame.Width:F0}×{frame.Height:F0}. Ближайший: '{best}'");
            return (printer, best);
        }
        public void ApplyToLayout(Layout layout, FrameInfo frame, ICommandContext ctx)
        {

            var psv = PlotSettingsValidator.Current;
            var (printer, media) = FindFormat(frame, ctx);

            if (printer == null || media == null)
            {
                ctx.LogWarn("  Принтер/формат не найдены — оставляем лист без настроек печати.");
                layout.ScaleLineweights = true;
                layout.PlotPlotStyles = true;
                return;
            }

            try
            {
                // Единицы
                psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);


                // Принтер + формат
                psv.SetPlotConfigurationName(layout, printer, media);
                psv.RefreshLists(layout);
                if (frame.NeedsSheetRotation)
                {
                    psv.SetPlotRotation(layout, PlotRotation.Degrees090);
                    ctx.LogInfo($"  PlotRotation: Rotate90 (альбом {frame.Width:F0}×{frame.Height:F0})");
                }
                else
                {
                    psv.SetPlotRotation(layout, PlotRotation.Degrees000);
                    ctx.LogInfo($"  PlotRotation: 0 (книга {frame.Width:F0}×{frame.Height:F0})");
                }
                // Область печати = весь лист
                // ВАЖНО: НЕ вызываем SetPlotCentered — несовместимо с PlotType.Layout!
                psv.SetPlotType(layout, PlotType.Layout);

                // Масштаб 1:1 (задаётся на Viewport, не на PlotSettings)
                // psv.SetUseStandardScale(layout, false);  ← не нужно, убираем
                // layout.CustomPrintScale = 1.0;           ← не нужно, VP уже 1:1

                // Базовые настройки печати
                layout.ScaleLineweights = true;
                layout.PlotPlotStyles = true;
                layout.DrawViewportsFirst = true;

                // Ч/б стиль
                try { psv.SetCurrentStyleSheet(layout, "monochrome.ctb"); } catch { }

                ctx.LogInfo($"  Печать: {printer}/{media} | PlotType=Layout | VP=1:1");
            }
            catch (Exception ex)
            {
                ctx.LogWarn($"  Ошибка ApplyToLayout: {ex.Message}");
                // Не бросаем — лист всё равно создан, VP настроен
            }
        }
        /// <summary>
        /// Нормализовать PageSetup раскладки непосредственно перед печатью: привязать
        /// валидный PC3 (DWG To PDF.pc3) с корректным форматом, PlotType=Layout и ч/б стиль.
        /// Используется, когда раскладка пришла из чертежа с некорректными настройками
        /// печати (PlotEngine.BeginPage бросает eInvalidPlotInfo). Layout должен быть
        /// открыт на запись в активной транзакции.
        /// </summary>
        public bool NormalizeForPdf(Layout layout, ICommandContext ctx, bool preferStandard = false)
        {
            try
            {
                var psv = PlotSettingsValidator.Current;
                string printer = ResolvePrinter(ctx);
                if (printer == null)
                {
                    ctx.LogWarn("  Нормализация: принтер DWG To PDF не найден.");
                    return false;
                }

                using var tmp = new PlotSettings(true);
                psv.SetPlotPaperUnits(tmp, PlotPaperUnit.Millimeters);
                var list = GetMediaNames(psv, tmp, printer, ctx);
                if (list.Count == 0) { ctx.LogWarn("  Нормализация: список форматов пуст."); return false; }

                // Пользовательские размеры ("UserDefinedMetric ...") хранятся не в чертеже,
                // а в конкретном PC3. Если такой формат был создан на другой машине/в другом
                // профиле, в текущем PC3 его нет: GetCanonicalMediaNameList его показывает,
                // SetPlotConfigurationName принимает, но PlotEngine.BeginPage бросает
                // eInvalidPlotInfo (не смог выделить бумагу). Для проблемных листов берём
                // только стандартный (встроенный) формат, гарантированно доступный устройству.
                string current = layout.CanonicalMediaName;
                string chosen;
                if (!preferStandard && !string.IsNullOrEmpty(current) &&
                    list.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)))
                {
                    chosen = current;   // нормальный путь: сохраняем штатный размер листа
                }
                else
                {
                    // preferStandard (повтор после eInvalidPlotInfo): берём только встроенные
                    // форматы С ПОЛЯМИ. Пользовательские могут отсутствовать в текущем PC3,
                    // а "full bleed" (без полей) даёт нулевую область печати -> eInvalidPlotInfo.
                    var cand = list;
                    if (preferStandard)
                    {
                        cand = list.Where(m => !IsCustomMedia(m) && !IsFullBleed(m)).ToList();
                        if (cand.Count == 0) cand = list.Where(m => !IsCustomMedia(m)).ToList();
                        if (cand.Count == 0) cand = list;
                    }
                    chosen = ChooseMedia(psv, tmp, printer, cand, layout.PlotPaperSize, ctx);
                }

                psv.SetPlotConfigurationName(layout, printer, chosen);
                psv.RefreshLists(layout);
                // Область печати кадрируем по плавающему viewport (рамке) как ОКНОМ, а не
                // PlotType=Layout. Layout печатает экстент ВСЕГО paper-space — вместе с
                // дефолтным viewport-#1 и случайной геометрией. На смещённых от начала координат
                // листах этот экстент оказывается БОЛЬШЕ формата (стр.2: 784x444 на А2 594x420),
                // и PlotEngine.BeginPage бросает eInvalidPlotInfo. Окно по viewport = ровно рамка,
                // совпадает с размером бумаги -> печать 1:1 проходит стабильно.
                string plotArea = "Layout";
                if (TrySetViewportPlotWindow(psv, layout, ctx)) plotArea = "Window(рамка)";
                else try { psv.SetPlotType(layout, PlotType.Layout); } catch { }
                try { psv.SetCurrentStyleSheet(layout, "monochrome.ctb"); } catch { }

                layout.ScaleLineweights = true;
                layout.PlotPlotStyles = true;
                layout.DrawViewportsFirst = true;

                ctx.LogInfo($"  Нормализация PageSetup: {printer}/{chosen} (область={plotArea}, ctb=monochrome).");
                return true;
            }
            catch (Exception ex)
            {
                ctx.LogWarn($"  Нормализация PageSetup не удалась: {ex.Message}");
                return false;
            }
        }

        // Установить область печати = прямоугольник плавающего viewport (рамки) в paper-space
        // как PlotType.Window. Возвращает false, если подходящего viewport нет (тогда вызывающий
        // откатится на PlotType.Layout). Координаты окна берутся из самого viewport, поэтому
        // работают при любой ориентации/смещении и не зависят от посторонней геометрии.
        private bool TrySetViewportPlotWindow(PlotSettingsValidator psv, Layout layout, ICommandContext ctx)
        {
            try
            {
                Database db = layout.Database;
                Transaction tr = db.TransactionManager.TopTransaction;
                if (tr == null) return false;

                var btr = (BlockTableRecord)tr.GetObject(
                    layout.BlockTableRecordId, OpenMode.ForRead);

                Viewport fp = null;
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Viewport v
                        && v.Number != 1 && !v.IsErased
                        && v.Width > 1.0 && v.Height > 1.0)
                    { fp = v; break; }
                }
                if (fp == null) return false;

                double cx = fp.CenterPoint.X, cy = fp.CenterPoint.Y;
                double hw = fp.Width / 2.0, hh = fp.Height / 2.0;
                var win = new Extents2d(cx - hw, cy - hh, cx + hw, cy + hh);

                // Порядок важен: сначала задаём окно, потом переключаем тип на Window.
                psv.SetPlotWindowArea(layout, win);
                psv.SetPlotType(layout, PlotType.Window);
                try { psv.SetPlotCentered(layout, false); } catch { }

                ctx.LogInfo($"  Область печати = окно рамки [{cx - hw:F0},{cy - hh:F0}]..[{cx + hw:F0},{cy + hh:F0}] " +
                            $"({fp.Width:F0}x{fp.Height:F0}).");
                return true;
            }
            catch (Exception ex)
            {
                ctx.LogDebug($"  окно по viewport не задано: {ex.Message}");
                return false;
            }
        }

        #region Private
        // Разрешить имя устройства печати: сначала расширенный PC3, при его
        // отсутствии — стковой DWG To PDF.pc3. null, если ни один не установлен.
        private string ResolvePrinter(ICommandContext ctx)
        {
            var devices = PlotSettingsValidator.Current.GetPlotDeviceList().Cast<string>()
                .Where(p => !string.IsNullOrWhiteSpace(p) && p != "Нет" && p != "None")
                .ToList();

            string printer = devices.FirstOrDefault(
                p => p.Equals(PrinterPrimary, StringComparison.OrdinalIgnoreCase));
            if (printer == null)
            {
                printer = devices.FirstOrDefault(
                    p => p.Equals(PrinterFallback, StringComparison.OrdinalIgnoreCase));
                if (printer != null)
                    ctx.LogWarn($"  '{PrinterPrimary}' не найден — откат на '{PrinterFallback}'.");
            }
            return printer;
        }

        // Пользовательский (созданный вручную в PC3) формат бумаги — не встроенный.
        // Такие имена не переносятся между машинами и вызывают eInvalidPlotInfo.
        private static bool IsCustomMedia(string media)
        {
            if (string.IsNullOrEmpty(media)) return false;
            return media.IndexOf("UserDefined", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("User-defined", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("User defined", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("пользовательск", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // "Full bleed" — печать без полей; у части PC3 у таких форматов нулевая
        // печатаемая область, из-за чего PlotEngine.BeginPage бросает eInvalidPlotInfo.
        private static bool IsFullBleed(string media)
        {
            if (string.IsNullOrEmpty(media)) return false;
            return media.IndexOf("bleed", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("холст", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("полей", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("без полей", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string ChooseMedia(PlotSettingsValidator psv, PlotSettings tmp, string printer,
            List<string> list, Point2d paper, ICommandContext ctx)
        {
            if (paper.X > 1.0 && paper.Y > 1.0)
            {
                string near = null; double best = double.MaxValue;
                foreach (string m in list)
                {
                    try
                    {
                        psv.SetPlotConfigurationName(tmp, printer, m);
                        psv.RefreshLists(tmp);
                        Point2d s = tmp.PlotPaperSize;
                        double d = Math.Abs(s.X - paper.X) + Math.Abs(s.Y - paper.Y);
                        if (d < best) { best = d; near = m; }
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(near)) return near;
            }
            foreach (string m in list) if (m.IndexOf("A4", StringComparison.OrdinalIgnoreCase) >= 0) return m;
            return list[0];
        }

        private List<string> GetMediaNames(
            PlotSettingsValidator psv, PlotSettings tmp, string printer, ICommandContext ctx)
        {
            // Порядок инициализации как в PlotterDiagnosticsService [12]
            foreach (string init in new[] { "", "A4", "Letter", "ISO_A4_(210.00_x_297.00_MM)" })
            {
                try
                {
                    psv.SetPlotConfigurationName(tmp, printer, init);
                    psv.RefreshLists(tmp);
                    var list = psv.GetCanonicalMediaNameList(tmp).Cast<string>().ToList();
                    if (list.Count > 0) return list;
                }
                catch { }
            }
            return new List<string>();
        }

        private string FindClosest(PlotSettingsValidator psv, PlotSettings tmp,
            string printer, double targetW, double targetH, List<string> mediaList, ICommandContext ctx)
        {
            string best = "";
            double bestDiff = double.MaxValue;

            // Фаза 1: найти наименьший формат, который ВМЕЩАЕТ рамку (в обеих ориентациях)
            foreach (string media in mediaList)
            {
                try
                {
                    psv.SetPlotConfigurationName(tmp, printer, media);
                    psv.RefreshLists(tmp);
                    Point2d size = tmp.PlotPaperSize;

                    // Проверяем вмещение: paper >= target в книжной OR альбомной ориентации
                    bool fitsLandscape = size.X >= targetW - 1.0 && size.Y >= targetH - 1.0;
                    bool fitsPortrait = size.Y >= targetW - 1.0 && size.X >= targetH - 1.0;

                    if (fitsLandscape || fitsPortrait)
                    {
                        // Площадь формата (минимизируем — берём наименьший вмещающий)
                        double area = size.X * size.Y;
                        if (area < bestDiff)
                        {
                            bestDiff = area;
                            best = media;
                        }
                    }
                }
                catch { }
            }

            // Фаза 2: если никто не вмещает — ближайший по евклидову расстоянию
            if (string.IsNullOrEmpty(best))
            {
                double bestDist = double.MaxValue;
                foreach (string media in mediaList)
                {
                    try
                    {
                        psv.SetPlotConfigurationName(tmp, printer, media);
                        psv.RefreshLists(tmp);
                        Point2d size = tmp.PlotPaperSize;
                        double dist = (targetW - size.X) * (targetW - size.X)
                                    + (targetH - size.Y) * (targetH - size.Y);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = media;
                        }
                    }
                    catch { }
                }
            }

            if (best == "") throw new Exception("Не удалось найти ни 1 ближайшего по площади принтера.");
            ctx.LogInfo($"Имя выбранного формата - {best}");
            return best;
        }
        #endregion
    }
}