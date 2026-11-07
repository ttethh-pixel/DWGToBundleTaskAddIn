using System;
using System.IO;

namespace DWGToBundleTaskAddIn
{
    // The AutoCAD plugin always writes <DWG>.pdf. Preserve a pre-existing local PDF
    // while converting, so rejection cannot replace or delete the previous result.
    internal sealed class LocalPdfGuard : IDisposable
    {
        private readonly string _path, _backup;
        private readonly FileAttributes _attributes;

        internal LocalPdfGuard(string path, string recoveryDirectory)
        {
            _path = path;
            if (!File.Exists(path)) return;
            _attributes = File.GetAttributes(path);
            Directory.CreateDirectory(recoveryDirectory);
            _backup = Path.Combine(recoveryDirectory, Guid.NewGuid().ToString("N") + ".pdf");
            File.Copy(path, _backup, false);
            // Leave the backup outside run-*; a failed restore must never be cleaned up.
            Logger.Log("Резервная копия локального PDF: " + path + " -> " + _backup);
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                if (_backup != null)
                {
                    if (File.Exists(_path)) File.SetAttributes(_path, FileAttributes.Normal);
                    File.Copy(_backup, _path, true);
                    File.SetAttributes(_path, _attributes);
                    File.SetAttributes(_backup, FileAttributes.Normal);
                    File.Delete(_backup);
                }
                else if (File.Exists(_path))
                {
                    File.SetAttributes(_path, FileAttributes.Normal);
                    File.Delete(_path);
                }
            }
            catch (Exception ex)
            {
                throw new IOException("Не удалось восстановить/очистить локальный PDF '" + _path +
                    "'. Резервная копия: " + (_backup ?? "отсутствует") + ". " + ex.Message, ex);
            }
        }
    }
}
