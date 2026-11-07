using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using SheetLayoutDistributor.Core;
using SheetLayoutDistributor.Core.Services;
using System;
using System.IO;

namespace SheetLayoutDistributor.Commands
{
    public class SheetLayoutCommands
    {
        [CommandMethod("SHEETS_TO_LAYOUT")]
        public void CreateLayoutsFromModel()
        {
            new LayoutOrchestrator().CreateLayoutsFromModel();
        }

        /// <summary>Полный цикл: рамка → листы → один монохромный PDF рядом с DWG.</summary>
        [CommandMethod("DWG_TO_PDF")]
        public void DwgToPdf()
        {
            // 1. Раскладка листов (детекция + вьюпорты + настройки печати).
            new LayoutOrchestrator().CreateLayoutsFromModel();

            // 2. Экспорт всех листов в PDF (PC3+CTB уже заданы → монохром, 1:1).
            var ctx = new CommandContext();
            ctx.InitLogger("export_log.txt");
            string outPdf = ResolveOutputPath(ctx);
            new PdfExportService().ExportSheetsToPdf(ctx, outPdf);
        }

        private static string ResolveOutputPath(CommandContext ctx)
        {
            string dwg = ctx.Db.Filename;
            if (!string.IsNullOrEmpty(dwg))
            {
                string dir = Path.GetDirectoryName(dwg);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(dwg) + ".pdf");
            }
            return Path.Combine(Path.GetTempPath(), "SheetLayoutDistributor.pdf");
        }
    }
}
