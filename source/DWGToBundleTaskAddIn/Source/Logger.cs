using System;
using System.IO;

namespace DWGToBundleTaskAddIn
{
    // Простой потокобезопасный файловый логгер. Пишет INFO и выше,
    // чтобы был виден реальный ход задачи при запуске из сессии 0 (PDM Agent).
    internal static class Logger
    {
        public enum LogLevel { Error = 0, Warning = 1, Info = 2, Verbose = 3 }

        private static readonly object Sync = new object();
        private static string _folder;
        private static string _file;
        private static LogLevel _min = LogLevel.Info;

        public static void Init(string folder, LogLevel min)
        {
            lock (Sync)
            {
                _folder = folder;
                _min = min;
                _file = Path.Combine(folder, "DWGToBundleTaskAddIn.log");
                try
                {
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                }
                catch { }
            }
        }

        public static string LogPath { get { lock (Sync) return _file; } }

        public static void Log(string msg, LogLevel level = LogLevel.Info)
        {
            try
            {
                if (level > _min) return;
                lock (Sync)
                {
                    if (string.IsNullOrEmpty(_file)) return;
                    // ротация по размеру
                    var fi = new FileInfo(_file);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    {
                        var bak = Path.Combine(_folder, "DWGToBundleTaskAddIn." +
                            DateTime.Now.ToString("dd.MM.yyyy") + ".log");
                        try { File.Move(_file, bak); } catch { }
                    }
                    using (var w = File.AppendText(_file))
                        w.WriteLine(string.Format("[{0}] {1}: {2}",
                            DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss.fff"), level, msg));
                }
            }
            catch { }
        }
    }
}
