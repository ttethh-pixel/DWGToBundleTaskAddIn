using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using SheetLayoutDistributor.Core.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace SheetLayoutDistributor.Core.Services
{
    public class CommandContext : ICommandContext
    {
        public Document Doc { get; }
        public Database Db { get; }
        public Editor Ed { get; }
        public LayoutManager Lm { get; }
        private string _logPath;
        private readonly object _lock = new();
        private bool _logInitialized;
        private bool _logWriteBroken;

        public CommandContext()
        {
            Doc = Application.DocumentManager.MdiActiveDocument;
            if (Doc == null) throw new InvalidOperationException("Нет активного документа AutoCAD.");
            Db = Doc.Database;
            Ed = Doc.Editor;
            Lm = LayoutManager.Current;
        }

        public void InitLogger(string fileName = "diagnostic_log.txt")
        {
            if (_logInitialized) return;

            string dllDir = GetDllDirectory();
            string targetDir = Path.Combine(dllDir, "logs");

            if (!TryEnsureDirectory(targetDir))
            {
                try
                {
                    if (!string.IsNullOrEmpty(Db.Filename))
                    {
                        string dwgDir = Path.GetDirectoryName(Db.Filename);
                        if (!string.IsNullOrEmpty(dwgDir) && Directory.Exists(dwgDir))
                            targetDir = dwgDir;
                    }
                }
                catch { }
            }

            if (!TryEnsureDirectory(targetDir))
            {
                targetDir = Path.GetTempPath();
            }

            string candidatePath = Path.Combine(targetDir, fileName);

            if (TryInitFile(candidatePath))
            {
                _logPath = candidatePath;
                _logInitialized = true;
            }
            else
            {
                string tempPath = Path.Combine(Path.GetTempPath(), fileName);
                if (!TryInitFile(tempPath))
                {
                    _logPath = null;
                    _logWriteBroken = true;
                    try { Ed.WriteMessage($"\n[LOG] Файл лога недоступен, пишем только в консоль."); }
                    catch { }
                }
                else
                {
                    _logPath = tempPath;
                    _logInitialized = true;
                }
            }
        }

        private string GetDllDirectory()
        {
            try
            {
                string codeBase = Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(codeBase);
            }
            catch
            {
                return Path.GetTempPath();
            }
        }

        private bool TryEnsureDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                using (var fs = new FileStream(Path.Combine(dir, ".write_test"), FileMode.Create, FileAccess.Write, FileShare.None)) { }
                File.Delete(Path.Combine(dir, ".write_test"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryInitFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.WriteLine($"=== SESSION {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                sw.WriteLine($"DWG: {Db.Filename}");
                sw.WriteLine($"DLL Path: {GetDllDirectory()}");
                sw.WriteLine();
                return true;
            }
            catch (Exception ex)
            {
                try { Ed.WriteMessage($"\n[LOG] Не удалось инициализировать '{path}': {ex.Message}"); }
                catch { }
                return false;
            }
        }

        public void Log(string message, string level = "INFO")
        {
            string safeMessage = message ?? "<null>";
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] {safeMessage}";

            try { Ed.WriteMessage($"\n{line}"); }
            catch { }

            if (!_logInitialized || _logWriteBroken || string.IsNullOrEmpty(_logPath)) return;
            if (safeMessage == null) return;

            try
            {
                lock (_lock)
                {
                    using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                    sw.WriteLine(line);
                    sw.Flush();
                }
            }
            catch (Exception ex)
            {
                if (!_logWriteBroken)
                {
                    _logWriteBroken = true;
                    try { Ed.WriteMessage($"\n[LOG WRITE DISABLED] {ex.GetType().Name}: {ex.Message}"); }
                    catch { }
                }
            }
        }

        public void LogInfo(string msg) => Log(msg, "INFO");
        public void LogDebug(string msg) => Log(msg, "DEBUG");
        public void LogWarn(string msg) => Log(msg, "WARN");
        public void LogError(string msg) => Log(msg, "ERROR");
        public void LogError(string msg, Exception ex)
        {
            LogError(msg);
            if (ex != null)
            {
                Log($"Exception: {ex.GetType().Name}: {ex.Message}", "ERROR");
                Log($"Stack: {ex.StackTrace}", "ERROR");
            }
        }

        public string GetLogPath() => _logPath;

        public void OpenLogFileInNotepad()
        {
            // В headless-режиме (accoreconsole) не всплываем Notepad.
            if (Environment.GetEnvironmentVariable("SLD_HEADLESS") == "1") return;
            try
            {
                if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return;
                Log($"Логи сохранены: {_logPath}");
                Process.Start(new ProcessStartInfo { FileName = _logPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                try { Ed.WriteMessage($"\n[LOG] Не удалось открыть notepad: {ex.Message}"); }
                catch { }
            }
        }
    }
}