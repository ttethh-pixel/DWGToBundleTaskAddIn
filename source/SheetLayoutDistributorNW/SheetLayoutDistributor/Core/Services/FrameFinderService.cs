using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SheetLayoutDistributor.Core.Interfaces;
using SheetLayoutDistributor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SheetLayoutDistributor.Core.Services
{
    public class FrameFinderService
    {
        #region Constants
        private const double MinSideLength = 140.0;
        private const double MaxSideLength = 2500.0;
        private const double ContainEpsilon = 0.01;
        private const double ColumnGroupTol = 30.0;
        #endregion

        #region Internal
        private struct RectCandidate
        {
            public double MinX, MinY, MaxX, MaxY;
            public readonly double Width => MaxX - MinX;
            public readonly double Height => MaxY - MinY;
            public readonly double CenterX => (MinX + MaxX) / 2.0;
            public readonly double CenterY => (MinY + MaxY) / 2.0;
            public ObjectId SourceId;
            public string SourceType;

            public readonly bool Contains(RectCandidate other)
            {
                return MinX <= other.MinX + ContainEpsilon
                    && MinY <= other.MinY + ContainEpsilon
                    && MaxX >= other.MaxX - ContainEpsilon
                    && MaxY >= other.MaxY - ContainEpsilon;
            }

            public readonly bool WithinLimits()
            {
                return Width >= MinSideLength && Width <= MaxSideLength
                    && Height >= MinSideLength && Height <= MaxSideLength;
            }
        }
        #endregion

        #region Public API
        public List<FrameInfo> FindFrames(ICommandContext ctx)
        {
            Database db = ctx.Db;
            var candidates = new List<RectCandidate>();

            using Transaction tr = db.TransactionManager.StartTransaction();
            try
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                // === Проход 1: полилинии НАПРЯМУЮ в ModelSpace ===
                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Polyline pl)
                    {
                        RectCandidate? rc = RectFromPolyline(pl, Matrix3d.Identity, id, "ModelSpace");
                        if (rc.HasValue) candidates.Add(rc.Value);
                    }
                }

                // === Проход 2: полилинии внутри BlockReference (любых блоков) ===
                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br) continue;
                    if (!br.Visible) continue;

                    // Трансформация блока в мировые координаты
                    Matrix3d blockXf = br.BlockTransform;

                    // Итерируем содержимое определения блока
                    var btrDef = (BlockTableRecord)tr.GetObject(
                        br.BlockTableRecord, OpenMode.ForRead);

                    CollectPolylinesFromBtr(tr, btrDef, blockXf, candidates, br.Name, ctx);

                    // Также проверяем вложенные BlockReference внутри определения
                    CollectNestedBlockRects(tr, btrDef, blockXf, candidates, br.Name, ctx);
                }

                ctx.LogInfo($"Собрано полилиний-кандидатов: {candidates.Count}");

                // Фильтр по размерам 140..2500
                candidates = candidates.Where(c => c.WithinLimits()).ToList();
                ctx.LogInfo($"После фильтра 140..2500: {candidates.Count}");

                // Удаление вложенных
                candidates = RemoveNested(candidates, ctx);
                ctx.LogInfo($"После удаления вложенных: {candidates.Count}");

                // Сортировка
                candidates = SortByGrid(candidates);

                // Результат
                var result = new List<FrameInfo>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    result.Add(new FrameInfo
                    {
                        Index = i + 1,
                        Center = new Point3d(c.CenterX, c.CenterY, 0),
                        BottomLeft = new Point3d(c.MinX, c.MinY, 0),
                        TopLeft = new Point3d(c.MinX, c.MaxY, 0),
                        BottomRight = new Point3d(c.MaxX, c.MinY, 0),
                        TopRight = new Point3d(c.MaxX, c.MaxY, 0),
                        Width = c.Width,
                        Height = c.Height,
                        SourceId = c.SourceId,
                        SourceType = c.SourceType
                    });
                    ctx.LogInfo($"  Рамка #{i + 1}: {c.Width:F1}×{c.Height:F1} " +
                                $"C=({c.CenterX:F1},{c.CenterY:F1}) [{c.SourceType}]");
                }

                tr.Commit();
                return result;
            }
            catch (Exception ex)
            {
                ctx.LogError("Ошибка поиска рамок", ex);
                tr.Abort();
                return new List<FrameInfo>();
            }
        }
        #endregion

        #region Collection from BTR
        private void CollectPolylinesFromBtr(
            Transaction tr, BlockTableRecord btr,
            Matrix3d transform, List<RectCandidate> candidates,
            string blockName, ICommandContext ctx)
        {
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
                if (!pl.Visible) continue;

                RectCandidate? rc = RectFromPolyline(pl, transform, id, $"Block:{blockName}");
                if (rc.HasValue) candidates.Add(rc.Value);
            }
        }

        private void CollectNestedBlockRects(
            Transaction tr, BlockTableRecord btr,
            Matrix3d parentXf, List<RectCandidate> candidates,
            string parentBlockName, ICommandContext ctx)
        {
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference innerBr) continue;
                if (!innerBr.Visible) continue;

                Matrix3d combined = parentXf * innerBr.BlockTransform;

                var innerDef = (BlockTableRecord)tr.GetObject(
                    innerBr.BlockTableRecord, OpenMode.ForRead);

                string label = $"Block:{parentBlockName}/{innerDef.Name}";
                CollectPolylinesFromBtr(tr, innerDef, combined, candidates, label, ctx);
            }
        }
        #endregion

        #region Rect From Polyline
        private RectCandidate? RectFromPolyline(Polyline pl, Matrix3d xf, ObjectId id, string src)
        {
            if (!pl.Closed) return null;
            if (pl.NumberOfVertices < 4) return null;

            Extents3d ext;
            try { ext = pl.GeometricExtents; } catch { return null; }

            // Трансформируем углы в мировые координаты [10]
            Point3d mn = new Point3d(ext.MinPoint.X, ext.MinPoint.Y, 0).TransformBy(xf);
            Point3d mx = new Point3d(ext.MaxPoint.X, ext.MaxPoint.Y, 0).TransformBy(xf);

            double minX = Math.Min(mn.X, mx.X);
            double minY = Math.Min(mn.Y, mx.Y);
            double maxX = Math.Max(mn.X, mx.X);
            double maxY = Math.Max(mn.Y, mx.Y);

            double w = maxX - minX;
            double h = maxY - minY;

            if (w < MinSideLength || w > MaxSideLength) return null;
            if (h < MinSideLength || h > MaxSideLength) return null;

            return new RectCandidate
            {
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                SourceId = id,
                SourceType = src
            };
        }
        #endregion

        #region Standard Frame Sizes (ГОСТ 2.301 / ISO 216)
        private static readonly (double W, double H)[] StandardSizes = new[]
        {
            (210.0, 297.0),   // A4
            (297.0, 420.0),   // A3
            (420.0, 594.0),   // A2
            (594.0, 841.0),   // A1
            (841.0, 1189.0),  // A0
        };
        private const double StandardTolMm = 5.0; // допуск определения "стандартного" размера

        private static bool IsStandardSize(RectCandidate c)
        {
            foreach (var (sw, sh) in StandardSizes)
            {
                // Проверка в обеих ориентациях
                if (Math.Abs(c.Width - sw) <= StandardTolMm && Math.Abs(c.Height - sh) <= StandardTolMm)
                    return true;
                if (Math.Abs(c.Width - sh) <= StandardTolMm && Math.Abs(c.Height - sw) <= StandardTolMm)
                    return true;
            }
            return false;
        }
        #endregion

        #region Nesting Removal
        private List<RectCandidate> RemoveNested(List<RectCandidate> candidates, ICommandContext ctx)
        {
            // Предвычислим, какие кандидаты имеют стандартный размер
            var isStd = new bool[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
                isStd[i] = IsStandardSize(candidates[i]);

            var removeSet = new HashSet<int>();

            for (int i = 0; i < candidates.Count; i++)
            {
                if (removeSet.Contains(i)) continue;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i == j || removeSet.Contains(j)) continue;
                    if (!candidates[i].Contains(candidates[j])) continue;

                    // i содержит j. Обычно удаляем j (вложенный), но:
                    // Если j — стандартный формат, а i — нет, удаляем i (нестандартная внешняя граница)
                    if (isStd[j] && !isStd[i])
                    {
                        removeSet.Add(i);
                        ctx.LogDebug($"  Внешняя нестандартная рамка {candidates[i].Width:F1}×{candidates[i].Height:F1} удалена (внутри — стандарт {candidates[j].Width:F1}×{candidates[j].Height:F1})");
                        break; // i помечен на удаление, переходим к следующему i
                    }
                    else
                    {
                        removeSet.Add(j);
                    }
                }
            }

            var result = candidates.Where((_, idx) => !removeSet.Contains(idx)).ToList();
            if (removeSet.Count > 0)
                ctx.LogDebug($"Удалено вложенных: {removeSet.Count}");
            return result;
        }
        #endregion

        #region Sorting
        private List<RectCandidate> SortByGrid(List<RectCandidate> list)
        {
            var colGroups = new List<List<RectCandidate>>();
            foreach (var c in list)
            {
                bool placed = false;
                foreach (var col in colGroups)
                {
                    if (Math.Abs(c.CenterX - col[0].CenterX) <= ColumnGroupTol)
                    {
                        col.Add(c);
                        placed = true;
                        break;
                    }
                }
                if (!placed) colGroups.Add(new List<RectCandidate> { c });
            }

            colGroups.Sort((a, b) =>
                a.Average(x => x.CenterX).CompareTo(b.Average(x => x.CenterX)));

            var result = new List<RectCandidate>();
            foreach (var col in colGroups)
            {
                col.Sort((a, b) => b.CenterY.CompareTo(a.CenterY));
                result.AddRange(col);
            }
            return result;
        }
        #endregion
    }
}