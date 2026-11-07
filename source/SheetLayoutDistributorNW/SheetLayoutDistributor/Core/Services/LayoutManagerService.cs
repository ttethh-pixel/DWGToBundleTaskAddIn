using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetLayoutDistributor.Core.Interfaces;
using SheetLayoutDistributor.Core.Models;
using System;
using System.Collections.Generic;

namespace SheetLayoutDistributor.Core.Services
{
    public class LayoutManagerService
    {
        private const string LayoutPrefix = "Лист_";

        public void CreateLayoutsFromFrames(
            List<FrameInfo> frames,
            PrinterConfigure printerConfig,
            ICommandContext ctx)
        {
            if (frames == null || frames.Count == 0) return;

            using DocumentLock docLock = ctx.Doc.LockDocument();
            Database db = ctx.Db;
            LayoutManager lm = ctx.Lm;
            string originalLayout = lm.CurrentLayout;

            int ok = 0, fail = 0;

            foreach (FrameInfo frame in frames)
            {
                string layoutName = LayoutPrefix + frame.Index;
                ctx.LogInfo($"=== '{layoutName}' | {frame.Width:F1}×{frame.Height:F1} " +
                            $"C=({frame.Center.X:F1},{frame.Center.Y:F1}) ===");

                try
                {
                    // === ФАЗА 1: Создать лист + настроить печать ===
                    ObjectId layoutId = CreateAndConfigureLayout(
                        db, lm, layoutName, frame, printerConfig, ctx);

                    // === ФАЗА 2: Переключиться на лист и настроить Viewport ===
                    // ОБЯЗАТЕЛЬНО: без переключения — eNotInPaperspace!
                    lm.CurrentLayout = layoutName;

                    ConfigureViewport(db, layoutId, frame, ctx);

                    ok++;
                    ctx.LogInfo($"  ✓ '{layoutName}'");
                }
                catch (Exception ex)
                {
                    fail++;
                    ctx.LogError($"  ✗ '{layoutName}'", ex);
                }
            }

            // Возврат на исходный лист
            try { lm.CurrentLayout = originalLayout; } catch { }
            ctx.LogInfo($"ИТОГО: {ok} создано, {fail} ошибок.");
        }

        #region Phase 1: Create + Configure
        private ObjectId CreateAndConfigureLayout(
        Database db, LayoutManager lm, string layoutName,
        FrameInfo frame, PrinterConfigure printerConfig, ICommandContext ctx)
        {
            DeleteLayoutIfExists(db, lm, layoutName, ctx);
            ObjectId layoutId = lm.CreateLayout(layoutName);
            using Transaction tr = db.TransactionManager.StartTransaction();
            try
            {
                Layout layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                printerConfig.ApplyToLayout(layout, frame, ctx);
                tr.Commit();
            }
            catch (Exception ex)
            {
                tr.Abort();
                ctx.LogWarn($"  Plot-настройки не применены: {ex.Message}");
                // НЕ бросаем — лист создан, Viewport будет настроен отдельно
            }

            return layoutId;
        }
        #endregion

        #region Phase 2: Viewport (требует CurrentLayout = лист)
        private void ConfigureViewport(
            Database db, ObjectId layoutId, FrameInfo frame, ICommandContext ctx)
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            try
            {
                Layout layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var btr = (BlockTableRecord)tr.GetObject(
                    layout.BlockTableRecordId, OpenMode.ForRead);

                // Ищем существующий VP (Number != 1) — как в оригинале [11]
                Viewport vp = null;
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Viewport v && v.Number != 1)
                    {
                        vp = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                        break;
                    }
                }

                // Если нет — создаём новый
                if (vp == null)
                {
                    vp = new Viewport();
                    btr.UpgradeOpen();
                    btr.AppendEntity(vp);
                    tr.AddNewlyCreatedDBObject(vp, true);
                }

                // Центр VP = центр листа
                vp.CenterPoint = new Point3d(frame.Width / 2.0, frame.Height / 2.0, 0.0);

                // Размер VP = размер листа
                vp.Width = frame.Width;
                vp.Height = frame.Height;

                // ViewCenter = центр рамки в модели
                vp.ViewCenter = new Point2d(frame.Center.X, frame.Center.Y);

                // Масштаб 1:1
                vp.CustomScale = 1.0;

                // Вид сверху
                vp.ViewDirection = Vector3d.ZAxis;
                vp.ViewTarget = new Point3d(0, 0, 0);
                vp.TwistAngle = 0.0;

                // Теперь безопасно — мы на PaperSpace [19]
                vp.On = true;
                vp.Locked = true;

                // Удаляем дефолтный viewport №1. На смещённых листах он наследует чужой
                // увеличенный вид (до 785×444 > формата), aspect-locked (resize не вписывает),
                // а On=false на активном №1 бросает исключение. Его прямоугольник раздувает
                // extents листа -> PlotEngine отклоняет PlotInfo (eInvalidPlotInfo).
                // Удаление №1 разрешено, когда на листе есть другой (наш рамочный) viewport;
                // остаётся ровно один плавающий viewport по рамке = корректная область печати.
                EraseDefaultViewport(btr, tr, vp, ctx);

                ctx.LogInfo($"  VP: C=({frame.Width / 2:F1},{frame.Height / 2:F1}) " +
                            $"size={frame.Width:F1}×{frame.Height:F1} " +
                            $"viewC=({frame.Center.X:F1},{frame.Center.Y:F1})");

                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }
        #endregion

        #region Helpers
        private void EraseDefaultViewport(BlockTableRecord btr, Transaction tr, Viewport frameVp, ICommandContext ctx)
        {
            try
            {
                ObjectId defId = ObjectId.Null;
                foreach (ObjectId id in btr)
                {
                    if (id == frameVp.ObjectId) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport v)) continue;
                    if (v.Number == 1 && !v.IsErased) { defId = id; break; }
                }
                if (defId.IsNull)
                {
                    ctx.LogDebug("  дефолтный viewport #1 отсутствует (уже удалён).");
                    return;
                }

                var dv = (Viewport)tr.GetObject(defId, OpenMode.ForWrite);
                ctx.LogDebug($"  удаляю viewport #1 (rect {dv.Width:F0}×{dv.Height:F0})...");
                dv.Erase();
                ctx.LogInfo("  ✓ дефолтный viewport #1 удалён — на листе остался только рамочный.");
            }
            catch (Exception ex)
            {
                // №1 остаётся активным (его нельзя удалить/отключить напрямую). Не роняем лист:
                // область печати задаётся окном по рамке, а неполный результат отсекут ворота PDF.
                ctx.LogDebug($"  viewport #1 не удалён: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void DeleteLayoutIfExists(Database db, LayoutManager lm, string name, ICommandContext ctx)
        {
            try
            {
                using Transaction tr = db.TransactionManager.StartTransaction();
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (dict.Contains(name))
                {
                    lm.DeleteLayout(name);
                    ctx.LogDebug($"Удалён '{name}'");
                }
                tr.Commit();
            }
            catch (Exception ex) { ctx.LogDebug($"Не удалён '{name}': {ex.Message}"); }
        }
        #endregion
    }
}