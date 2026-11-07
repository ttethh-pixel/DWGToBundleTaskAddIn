using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace DWGToBundleTaskAddIn
{
    internal static class EngineExtractor
    {
        private const string ResBat = "dwg2pdf.bat";
        private const string ResDll = "SheetLayoutDistributor.dll";

        public static string EnsureEngine(out string dir)
        {
            dir = "";
            try
            {
                // Each compiled add-in gets its own immutable engine directory.
                var assembly = Assembly.GetExecutingAssembly();
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DWGToBundleTaskAddIn", "engine", assembly.ManifestModule.ModuleVersionId.ToString("N"));
                Directory.CreateDirectory(dir);
                Extract(assembly, ResBat, Path.Combine(dir, ResBat));
                Extract(assembly, ResDll, Path.Combine(dir, ResDll));
                return Path.Combine(dir, ResBat);
            }
            catch (Exception ex)
            {
                Logger.Log("EnsureEngine: " + ex, Logger.LogLevel.Error);
                return "";
            }
        }

        private static void Extract(Assembly assembly, string resource, string target)
        {
            string resourceName = null;
            foreach (string name in assembly.GetManifestResourceNames())
                if (name.EndsWith("." + resource, StringComparison.OrdinalIgnoreCase)) { resourceName = name; break; }
            if (resourceName == null) throw new IOException("В сборке отсутствует " + resource);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var src = assembly.GetManifestResourceStream(resourceName))
                using (var dest = File.Create(temporary))
                {
                    if (src == null) throw new IOException("Невозможно прочитать " + resource);
                    src.CopyTo(dest);
                }
                if (File.Exists(target))
                {
                    if (!SameContent(temporary, target)) throw new IOException("Встроенный движок изменён на диске: " + target);
                    return;
                }
                try { File.Move(temporary, target); }
                catch (IOException)
                {
                    // Another process may have completed the same extraction.
                    if (!File.Exists(target) || !SameContent(temporary, target)) throw;
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static bool SameContent(string left, string right)
        {
            if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
            using (var sha = SHA256.Create())
            using (var l = File.OpenRead(left))
            using (var r = File.OpenRead(right))
            {
                byte[] a = sha.ComputeHash(l), b = sha.ComputeHash(r);
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }
        }
    }
}
