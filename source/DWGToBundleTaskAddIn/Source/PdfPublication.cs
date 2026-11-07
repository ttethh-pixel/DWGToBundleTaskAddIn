using System;
using System.IO;
using System.Security.Cryptography;

namespace DWGToBundleTaskAddIn
{
    internal sealed class ArchivePdf
    {
        internal int Id, FolderId, Version;
        internal bool Locked;
        internal string LocalPath;
    }

    internal interface IPdfArchive
    {
        ArchivePdf Find(string output);
        ArchivePdf Add(string output, string validatedPdf);
        void UnregisterForEditing(ArchivePdf file);
        void RegisterEditedFile(ArchivePdf file);
        void CancelUnregistration(ArchivePdf file);
        void ReadVersion(ArchivePdf file, string destination);
    }

    internal static class PdfPublication
    {
        // validateArchived: необязательная проверка СОДЕРЖИМОГО скачанной из архива копии
        // (путь -> число непустых страниц; бросает исключение, если PDF битый/белый).
        // Нужна потому, что PDM может байт-за-байтом изменить PDF при помещения в архив
        // (нормализация/метаданные), хотя документ содержательно корректен. Тогда строгое
        // сравнение хешей даёт ложный брак; в этом случае принимаем по содержимому.
        internal static void Save(IPdfArchive archive, string output, string validatedPdf, string scratch,
            Func<string, int> validateArchived)
        {
            string expectedHash = Hash(validatedPdf);
            ArchivePdf before = archive.Find(output);
            bool ownsCheckout = false;
            bool isNew = before == null;
            ArchivePdf target = before;
            try
            {
                if (isNew)
                {
                    target = archive.Add(output, validatedPdf);
                    if (target == null || target.Id <= 0) throw new IOException("PDM не подтвердил добавление PDF.");
                    if (target.Locked) archive.RegisterEditedFile(target);
                }
                else
                {
                    if (before.Locked) throw new IOException("PDF уже разрегистрирован. Существующий файл не изменён.");
                    archive.UnregisterForEditing(before); // PDM: разрегистрация PDF
                    ownsCheckout = true;
                    target = archive.Find(output);
                    if (target == null || target.Id != before.Id || !target.Locked || target.Version != before.Version)
                        throw new IOException("Состояние PDF изменилось при разрегистрации. Запись отменена.");
                    File.Copy(validatedPdf, target.LocalPath, true);
                    archive.RegisterEditedFile(target); // PDM: регистрация новой версии
                }

                ArchivePdf saved = archive.Find(output);
                if (saved == null || saved.Id != target.Id || saved.Locked || saved.Version < 1 ||
                    (!isNew && saved.Version <= before.Version))
                    throw new IOException("PDM не подтвердил возврат PDF в архив.");
                ownsCheckout = false;

                // Read this exact archived version into a new path, never trust the local cache.
                string verify = Path.Combine(scratch, "verify-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    archive.ReadVersion(saved, verify);
                    if (Hash(verify) != expectedHash)
                    {
                        // Байты в архиве отличаются от локального результата. Это допустимо,
                        // только если PDM изменил PDF несущественно (нормализация, метаданные).
                        // Проверяем содержимое архивной копии так же строго, как локальную.
                        if (validateArchived == null)
                            throw new IOException("Содержимое PDF в архиве не совпало с проверенным результатом.");
                        int archivedPages = validateArchived(verify);
                        Logger.Log("PDF в архиве отличается от локального байт-в-байт (PDM изменил при помещении), " +
                            "но содержимое провалидировано: непустых страниц " + archivedPages + ". Принимаем.", Logger.LogLevel.Info);
                    }
                    ArchivePdf current = archive.Find(output);
                    if (current == null || current.Id != saved.Id || current.Version != saved.Version || current.Locked)
                        throw new IOException("PDF изменился в PDM во время проверки сохранения.");
                }
                finally { DeleteTemporary(verify); }
            }
            catch (Exception error)
            {
                // Undo only our checkout of a previously committed file, never another user's
                // checkout and never delete a newly added file or an existing archived version.
                if (ownsCheckout && before != null)
                {
                    try
                    {
                        ArchivePdf current = archive.Find(output);
                        if (current != null && current.Id == before.Id && current.Locked && current.Version == before.Version)
                            archive.CancelUnregistration(before);
                    }
                    catch (Exception rollback)
                    {
                        throw new IOException(error.Message + " Не удалось отменить нашу разрегистрацию PDF: " + rollback.Message, error);
                    }
                }
                throw;
            }
        }

        internal static void SaveOutsideVault(string output, string validatedPdf)
        {
            string directory = Path.GetDirectoryName(output);
            Directory.CreateDirectory(directory);
            string staging = Path.Combine(directory, ".dwg2pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(validatedPdf, staging, false);
                if (Hash(staging) != Hash(validatedPdf)) throw new IOException("Не удалось проверить копию PDF.");
                if (File.Exists(output)) File.Replace(staging, output, null);
                else File.Move(staging, output);
                if (Hash(output) != Hash(validatedPdf)) throw new IOException("PDF результата не совпал с проверенной копией.");
            }
            finally { DeleteTemporary(staging); }
        }

        private static string Hash(string path)
        {
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(file));
        }

        private static void DeleteTemporary(string path)
        {
            try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch { }
        }
    }
}
