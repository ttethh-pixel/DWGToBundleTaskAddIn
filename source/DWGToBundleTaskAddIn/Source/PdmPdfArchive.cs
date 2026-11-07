using System;
using System.IO;
using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    internal sealed class PdmPdfArchive : IPdfArchive
    {
        private readonly IEdmVault11 _vault;
        internal PdmPdfArchive(IEdmVault11 vault) { _vault = vault; }

        public ArchivePdf Find(string output)
        {
            IEdmFolder5 folder;
            var file = _vault.GetFileFromPath(output, out folder);
            if (file == null) return null;
            file.Refresh();
            if (folder == null) throw new IOException("PDM не определил папку PDF.");
            return new ArchivePdf { Id = file.ID, FolderId = folder.ID, Version = file.CurrentVersion,
                Locked = file.IsLocked, LocalPath = file.GetLocalPath(folder.ID) };
        }

        public ArchivePdf Add(string output, string validatedPdf)
        {
            string directory = Path.GetDirectoryName(output);
            var parent = _vault.GetFolderFromPath(directory);
            if (parent == null)
            {
                var create = (IEdmBatchAdd)_vault.CreateUtility(EdmUtility.EdmUtil_BatchAdd);
                create.AddFolderPath(directory, 0);
                EdmFileInfo[] folders = null;
                CheckResults(create.CommitAdd(0, ref folders, 0, new EdmCallbackImpl()), folders);
                parent = _vault.GetFolderFromPath(directory);
            }
            if (parent == null || !parent.HasRightsEx(8)) throw new UnauthorizedAccessException("Нет прав добавить PDF в " + directory);
            // Add() вызывается только когда PDM не знает о файле (Save->Find вернул null).
            // Если по пути результата на диске агента всё же лежит файл, это осиротевшая
            // рабочая копия: PDF был удалён из архива пользователем, но локальный файл не
            // снесён, и он блокирует BatchAdd. Такой НЕотслеживаемый файл безопасно убрать.
            // Отслеживаемый PDM-файл сюда попасть не может — он идёт веткой обновления версии.
            if (File.Exists(output))
            {
                if (Find(output) != null)
                    throw new IOException("PDM уже содержит PDF по пути результата (ожидалось обновление версии): " + output);
                RemoveUntrackedLocal(output);
            }
            var add = (IEdmBatchAdd)_vault.CreateUtility(EdmUtility.EdmUtil_BatchAdd);
            add.AddFileFromPath(validatedPdf, parent.ID);
            EdmFileInfo[] added = null;
            CheckResults(add.CommitAdd(0, ref added, 0, new EdmCallbackImpl()), added);
            var result = Find(output);
            bool found = false;
            if (result != null && added != null)
                foreach (var item in added)
                    if (item.mlFileID == result.Id && item.mlFolderID == parent.ID) found = true;
            if (!found) throw new IOException("CommitAdd не подтвердил ожидаемый PDF: " + output);
            return result;
        }

        // Удаляет НЕотслеживаемый PDM локальный файл, оставшийся после удаления PDF из
        // архива. Сначала снимает read-only, при неудаче переносит в карман pdf-recovery,
        // чтобы BatchAdd не натыкался на прежнюю копию. Если убрать не удалось — понятная
        // ошибка с путём для ручной чистки (в архив при этом ничего не пишется).
        private static void RemoveUntrackedLocal(string output)
        {
            try { File.SetAttributes(output, FileAttributes.Normal); } catch { }
            try
            {
                File.Delete(output);
                Logger.Log("Удалён осиротевший локальный PDF вне PDM: " + output, Logger.LogLevel.Warning);
                return;
            }
            catch (IOException deleteError)
            {
                string recovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DWGToBundleTaskAddIn", "pdf-recovery");
                try
                {
                    Directory.CreateDirectory(recovery);
                    string move = Path.Combine(recovery, DateTime.Now.ToString("yyyyMMdd-HHmmss-") +
                        Path.GetFileName(output));
                    File.Move(output, move);
                    Logger.Log("Осиротевший локальный PDF перемещён в " + move + " (не удалялся: " +
                        deleteError.Message + ")", Logger.LogLevel.Warning);
                    return;
                }
                catch { }
                throw new IOException("Не удалось убрать локальный PDF вне PDM, он блокирует добавление: " +
                    output + ". Удалите его вручную. " + deleteError.Message, deleteError);
            }
        }

        // IEdmBatchAdd.CommitAdd возвращает ЧИСЛО добавленных объектов (1 = успех),
        // а не код ошибки. Ошибкой является только отрицательный результат либо
        // ненулевой mhResult у конкретного элемента.
        private static void CheckResults(int result, EdmFileInfo[] files)
        {
            if (result < 0) throw new IOException("Ошибка CommitAdd: " + result);
            if (files != null)
                foreach (var file in files)
                    if (file.mhResult != 0) throw new IOException("Ошибка CommitAdd для ID=" + file.mlFileID + ": " + file.mhResult);
        }

        private IEdmFile5 Get(ArchivePdf pdf)
        {
            var file = _vault.GetObject(EdmObjectType.EdmObject_File, pdf.Id) as IEdmFile5;
            if (file == null) throw new IOException("PDF больше не найден в PDM: ID=" + pdf.Id);
            file.Refresh();
            return file;
        }

        // Разрегистрировать существующий файл: IEdmFile5.LockFile.
        public void UnregisterForEditing(ArchivePdf pdf)
        {
            var file = Get(pdf);
            if (file.IsLocked || file.CurrentVersion != pdf.Version) throw new IOException("PDF занят или изменился перед обновлением.");
            // LockFile does not guarantee that this agent already has the latest bytes.
            object version = 0;
            object folder = pdf.FolderId;
            file.GetFileCopy(0, ref version, ref folder, (int)EdmGetFlag.EdmGet_Simple, "");
            file.LockFile(pdf.FolderId, 0);
        }
        // Зарегистрировать новую версию того же ID: IEdmFile5.UnlockFile.
        public void RegisterEditedFile(ArchivePdf pdf) { Get(pdf).UnlockFile(0, "DWG->PDF: проверенный PDF", 0, null); }
        public void CancelUnregistration(ArchivePdf pdf) { Get(pdf).UndoLockFile(0, true); }
        public void ReadVersion(ArchivePdf pdf, string destination)
        {
            object version = pdf.Version;
            object directory = Path.GetDirectoryName(destination) + Path.DirectorySeparatorChar;
            Get(pdf).GetFileCopy(0, ref version, ref directory, (int)EdmGetFlag.EdmGet_Simple, Path.GetFileName(destination));
        }
    }
}
