using SheetLayoutDistributor.Core.Interfaces;
using SheetLayoutDistributor.Core.Models;
using SheetLayoutDistributor.Core.Services;
using System;
using System.Collections.Generic;

namespace SheetLayoutDistributor.Core
{
    public class LayoutOrchestrator : ILayoutService
    {

        public void CreateLayoutsFromModel()
        {
            var ctx = new CommandContext();
            ctx.InitLogger();
            ctx.LogInfo("=== SHEETS_TO_LAYOUT ===");
            SwitchToModelSpace(ctx);
            try
            {
                // Переключаемся на модель
                ctx.Lm.CurrentLayout = "Model";

                // 1. Поиск рамок
                var frameFinder = new FrameFinderService();
                List<FrameInfo> frames = frameFinder.FindFrames(ctx);

                if (frames.Count == 0)
                {
                    ctx.LogWarn("Рамки не найдены.");
                    return;
                }

                ctx.LogInfo($"Найдено рамок: {frames.Count}");

                // 2. Создание листов
                var printerConfig = new PrinterConfigure();
                var layoutService = new LayoutManagerService();
                layoutService.CreateLayoutsFromFrames(frames, printerConfig, ctx);

                ctx.LogInfo("=== Завершено ===");
            }
            catch (Exception ex)
            {
                ctx.LogError("Критическая ошибка", ex);
            }
            finally
            {
                ctx.OpenLogFileInNotepad();
            }
        }

        #region Private
        private void SwitchToModelSpace(ICommandContext ctx)
        {
            ctx.Lm.CurrentLayout = "Model";
        }
        #endregion
    }
}