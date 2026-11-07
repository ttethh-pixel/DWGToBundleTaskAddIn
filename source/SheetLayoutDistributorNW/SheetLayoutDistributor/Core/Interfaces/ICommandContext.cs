using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace SheetLayoutDistributor.Core.Interfaces
{
    public interface ICommandContext
    {
        Document Doc { get; }
        Database Db { get; }
        Editor Ed { get; }
        LayoutManager Lm { get; }

        void InitLogger(string fileName = null);
        void Log(string message, string level = "INFO");
        void LogInfo(string message);
        void LogWarn(string message);
        void LogError(string message);
        void LogError(string message, System.Exception ex);
        void LogDebug(string message);
        string GetLogPath();
        void OpenLogFileInNotepad();
    }
}