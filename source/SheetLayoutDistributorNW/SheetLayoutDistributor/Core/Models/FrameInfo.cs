using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace SheetLayoutDistributor.Core.Models
{
    /// <summary>
    /// Информация о геометрии рамки листа.
    /// </summary>
    /// 
    public class FrameInfo
    {
        /// <summary>Порядковый номер рамки (1..N).</summary>
        public int Index { get; set; }

        /// <summary>Центр рамки в мировых координатах.</summary>
        public Point3d Center { get; set; }

        /// <summary>Левый нижний угол рамки в мировых координатах.</summary>
        public Point3d BottomLeft { get; set; }

        /// <summary>Левый верхний угол рамки в мировых координатах.</summary>
        public Point3d TopLeft { get; set; }

        /// <summary>Правый нижний угол.</summary>
        public Point3d BottomRight { get; set; }

        /// <summary>Правый верхний угол.</summary>
        public Point3d TopRight { get; set; }

        /// <summary>Ширина рамки (мм).</summary>
        public double Width { get; set; }

        /// <summary>Высота рамки (мм).</summary>
        public double Height { get; set; }

        /// <summary>Относительная ошибка ширины (доля, напр. -0.0186 = -1.86%).</summary>
        public double RelErrorW { get; set; }

        /// <summary>Относительная ошибка высоты.</summary>
        public double RelErrorH { get; set; }

        /// <summary>ObjectId источника.</summary>
        public ObjectId SourceId { get; set; }

        /// <summary>Тип источника: "Polyline" или "Block:имя".</summary>
        public string SourceType { get; set; }

        public bool NeedsSheetRotation { get; set; }

        public override string ToString() =>
            $"[{Index}] BL=({BottomLeft.X:F2},{BottomLeft.Y:F2}) " +
            $"C=({Center.X:F2},{Center.Y:F2}) " +
            $"{Width:F1}×{Height:F1} [{SourceType}]";
    }
}