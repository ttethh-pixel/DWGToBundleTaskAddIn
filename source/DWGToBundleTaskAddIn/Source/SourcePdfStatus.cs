using System;
using System.Globalization;
using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    // Выставляет в карточке исходного DWG/DXF версия-независимый признак pdf:
    //   "0" — корректного PDF нет (пустой/битый лист, ошибка конвертации,
    //         неподтверждённое сохранение) — публикация не состоялась;
    //   "1" — корректный PDF получен и опубликован.
    // Поток в PDM настроен именно на этот признак.
    internal static class SourcePdfStatus
    {
        internal static void MarkRejected(IEdmVault11 vault, int sourceId, int folderId, string varName)
        {
            Set(vault, sourceId, folderId, Normalize(varName), "0");
        }

        internal static void MarkAccepted(IEdmVault11 vault, int sourceId, int folderId, string varName)
        {
            Set(vault, sourceId, folderId, Normalize(varName), "1");
        }

        private static string Normalize(string varName)
        {
            return string.IsNullOrWhiteSpace(varName) ? Cfg.DefStatusVar : varName.Trim();
        }

        private static void Set(IEdmVault11 vault, int sourceId, int folderId, string varName, string expected)
        {
            string label = varName + "=" + expected;
            var file = vault.GetObject(EdmObjectType.EdmObject_File, sourceId) as IEdmFile5;
            if (file == null) throw new InvalidOperationException("Исходный DWG/DXF не найден в PDM.");
            var manager = (IEdmVariableMgr5)vault;
            var variable = manager.GetVariable(varName);
            if (variable == null) throw new InvalidOperationException("В PDM отсутствует переменная '" + varName + "'.");
            int versionFree = (int)EdmVariableFlags.EdmVar_VerFreeUpdateAll | (int)EdmVariableFlags.EdmVar_VerFreeUpdateLatest;
            if ((variable.Flags & versionFree) == 0)
                throw new InvalidOperationException("Переменная '" + varName + "' должна быть настроена как «без версии» (Version free).");
            if (file.IsLocked)
                throw new InvalidOperationException("Исходный DWG/DXF извлечён: верните файл в архив и повторите задачу для записи " + label + ".");

            var update = vault.CreateUtility(EdmUtility.EdmUtil_BatchUpdate) as IEdmBatchUpdate2;
            if (update == null) throw new InvalidOperationException("PDM не предоставил BatchUpdate.");
            object value = expected;
            update.SetVar(sourceId, variable.ID, ref value, "", (int)EdmBatchFlags.EdmBatch_AllConfigs);
            EdmBatchError2[] errors;
            int count = update.CommitUpdate(out errors, null);
            if (count != 0 || (errors != null && errors.Length != 0))
                throw new InvalidOperationException("PDM отклонил запись " + label + "; ошибок BatchUpdate: " + count +
                    ". Проверьте карточку и права изменения переменных без версии в папке и состоянии.");

            // Read back from PDM after committing; SetVar can otherwise silently do nothing.
            object version = 0;
            IEdmStrLst5 configurations = file.GetConfigurations(ref version);
            var names = new System.Collections.Generic.List<string>();
            if (configurations != null)
            {
                var pos = configurations.GetHeadPosition();
                while (pos != null && !pos.IsNull) names.Add(configurations.GetNext(pos));
            }
            if (names.Count == 0) names.Add("");
            var reader = file.GetEnumeratorVariable() as IEdmEnumeratorVariable10;
            if (reader == null) throw new InvalidOperationException("PDM не предоставил чтение переменных карточки.");
            try
            {
                foreach (string name in names)
                {
                    object read;
                    if (!reader.GetVar2(varName, name, folderId, out read) || read == null ||
                        Convert.ToString(read, CultureInfo.InvariantCulture) != expected)
                        throw new InvalidOperationException("Не подтверждена запись " + label + " в конфигурации '" + name + "'.");
                }
            }
            finally { reader.CloseFile(false); }
            Logger.Log("Исходный файл ID=" + sourceId + ": " + label + " записано и проверено.");
        }
    }
}

