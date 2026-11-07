using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    // Исполнитель задачи: для каждого DWG/DXF запускает dwg2pdf.bat (в рабочей
    // папке чертежа, чтобы разрешились внешние ссылки), забирает итоговый ч/б
    // PDF и публикует его через IEdmBatchAdd (новый файл) или LockFile/UnlockFile
    // (новая версия существующего файла).
    internal class BundleTaskExecutor
    {
        private static readonly string[] Accepted = { ".dwg", ".dxf" };

        private readonly IEdmVault11 _vault;
        private IEdmTaskInstance _instance;

        private string _bat, _acc, _gs, _outTpl, _errLog, _script, _prefix, _suffix, _statusVar, _transition;
        private int _timeoutMin;
        private bool _linkPdfToSource = true; // связывать PDF с исходным DWG (Связанные файлы PDM)

        public BundleTaskExecutor(IEdmVault5 vault) { _vault = vault as IEdmVault11; }

        private class FileItem
        {
            public int SourceFileId;
            public int SourceFolderId;
            public string SourcePath;
            public string OutputPath;     // итоговый путь в архиве (.pdf)
            public string TempPdf;        // локальный временный PDF для AddFileFromPath
            public bool Success;
            public string Error;
        }

        public void OnTaskRun(ref EdmCmd poCmd, ref EdmCmdData[] ppoData)
        {
            _instance = poCmd.mpoExtra as IEdmTaskInstance;
            var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "DWGToBundleTaskAddIn", "run-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (!ReadSettings()) { Fail("Не удалось подготовить движок (нет встроенного dwg2pdf.bat и не задан путь/сценарий)."); return; }
                Directory.CreateDirectory(temp);
                if (!string.IsNullOrWhiteSpace(_script))
                {
                    _bat = Path.Combine(temp, "dwg2pdf_task.bat");
                    File.WriteAllText(_bat, _script, Encoding.Default);
                    Logger.Log("Код сценария задачи записан во временный bat: " + _bat);
                }
                Logger.Log(string.Format("TaskRun start. bat={0} | acc={1} | gs={2} | tpl={3} | timeout={4}m",
                    _bat, Def(_acc), Def(_gs), _outTpl, _timeoutMin));

                var files = Collect(ppoData);
                if (files.Count == 0)
                {
                    _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneOK, 0, "Net DWG/DXF faylov dlya obrabotki.", Type.Missing);
                    return;
                }
                _instance.SetProgressRange(files.Count, 0, "Start...");
                int done = 0;
                foreach (var f in files)
                {
                    if (CheckCancelOrSuspend()) return;
                    _instance.SetProgressPos(done, "Konvertaciya " + Path.GetFileName(f.SourcePath));
                    ProcessOne(poCmd, f, temp);
                    done++;
                }

                int ok = 0; foreach (var f in files) if (f.Success) ok++;
                if (ok == files.Count)
                    _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneOK, 0, string.Format("Gotovo: {0} fail(ov) -> PDF.", ok), Type.Missing);
                else
                {
                    var sb = new StringBuilder();
                    foreach (var f in files) if (!f.Success) sb.Append(Path.GetFileName(f.SourcePath)).Append(": ").Append(f.Error).Append('\n');
                    _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneFailed, 0,
                        string.Format("Uspešno {0} iz {1}:\n{2}", ok, files.Count, sb), Type.Missing);
                }
                WriteErrorLog(files);
            }
            catch (COMException ex)
            {
                string n, d; _vault.GetErrorString(ex.ErrorCode, out n, out d);
                try { _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneFailed, ex.ErrorCode, d, Type.Missing); } catch { }
                Logger.Log("TaskRun COM: " + n + " | " + d, Logger.LogLevel.Error);
            }
            catch (Exception ex)
            {
                try { _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneFailed, 0, ex.Message, Type.Missing); } catch { }
                Logger.Log("TaskRun: " + ex, Logger.LogLevel.Error);
            }
            finally { TryDeleteDir(temp); }
        }

        private List<FileItem> Collect(EdmCmdData[] data)
        {
            var list = new List<FileItem>();
            if (data == null) return list;
            foreach (var d in data)
            {
                if (d.mlLongData1 != 1) continue; // только файлы
                var path = d.mbsStrData1;
                if (string.IsNullOrEmpty(path)) continue;
                var ext = Path.GetExtension(path);
                bool okExt = false;
                foreach (var a in Accepted) if (string.Equals(a, ext, StringComparison.OrdinalIgnoreCase)) { okExt = true; break; }
                if (!okExt) { Logger.Log("Propusk (ne DWG/DXF): " + path, Logger.LogLevel.Info); continue; }
                list.Add(new FileItem { SourceFileId = d.mlObjectID1, SourceFolderId = d.mlObjectID2, SourcePath = path });
            }
            return list;
        }

        private static string Def(string s) { return string.IsNullOrEmpty(s) ? "(avto)" : s; }
        private static string Str(object o, string def)
        {
            if (o == null || o is DBNull) return def;
            var s = o.ToString(); return string.IsNullOrEmpty(s) ? def : s;
        }

        // Автопочинка характерного «двойного кодирования»: исходные UTF-8 байты были
        // прочитаны как OEM CP866 (видны символы рамочного блока U+2500..U+259F, напр.
        // «╨╜╨╛╨▓╤Л╨╜» вместо «новый»). Такие строки попадали в сохранённые настройки задачи
        // из ранее собранных DLL. Обратное преобразование: CP866 -> байты -> UTF-8.
        private static string RepairMojibake(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            bool suspect = false;
            foreach (char c in s) if (c >= 0x2500 && c <= 0x259F) { suspect = true; break; }
            if (!suspect) return s;
            try
            {
                var cp866 = Encoding.GetEncoding(866);
                byte[] raw = cp866.GetBytes(s);
                string fixedS = Encoding.UTF8.GetString(raw);
                if (fixedS.IndexOf('\uFFFD') < 0 && fixedS != s)
                {
                    Logger.Log("Output Path: исправлена кодировка (UTF-8 как CP866): [" + s + "] -> [" + fixedS + "]", Logger.LogLevel.Warning);
                    return fixedS;
                }
            }
            catch (Exception ex) { Logger.Log("RepairMojibake: " + ex.Message, Logger.LogLevel.Warning); }
            return s;
        }

        // Сервер обязан конвертировать ФАКТИЧЕСКИ последнюю версию DWG/DXF, на которой
        // запущена задача. Раньше использовалось сравнение CurrentVersion с номером
        // локального файла (GetLocalVersionNo): оно ненадёжно — локальный кэш агента может
        // числиться актуальным, а байты на диске оставаться от прежней версии, из-за чего
        // PDF строился по устаревшему чертежу. Теперь всегда принудительно выкачиваем
        // текущую (последнюю) версию поверх локальной копии перед конвертацией.
        private void GetLatestVersions(EdmCmd poCmd, List<FileItem> files)
        {
            foreach (var f in files)
            {
                var file = _vault.GetObject(EdmObjectType.EdmObject_File, f.SourceFileId) as IEdmFile5;
                if (file == null) continue;
                try { file.Refresh(); } catch { }
                int folderId = f.SourceFolderId;
                string local = file.GetLocalPath(folderId);
                if (file.IsLocked)
                {
                    // Исходник снят с регистрации — рабочую копию не затираем, конвертируем как есть.
                    Logger.Log("GetLatestVersions: " + Path.GetFileName(local) +
                        " извлечён (check-out) — принудительная выкачка пропущена.");
                    f.SourcePath = local;
                    continue;
                }
                try
                {
                    if (File.Exists(local)) { File.SetAttributes(local, FileAttributes.Normal); File.Delete(local); }
                    object version = 0;            // 0 = последняя зафиксированная версия
                    object folder = folderId;
                    file.GetFileCopy(poCmd.mlParentWnd, ref version, ref folder,
                        (int)EdmGetFlag.EdmGet_Simple, Path.GetFileName(local));
                    Logger.Log("GetLatestVersions: выкачана последняя версия " +
                        Path.GetFileName(local) + " v" + file.CurrentVersion + ".");
                }
                catch (Exception ex)
                {
                    Logger.Log("GetLatestVersions " + Path.GetFileName(local) + ": " + ex.Message,
                        Logger.LogLevel.Warning);
                }
                f.SourcePath = file.GetLocalPath(folderId);
            }
        }

        private bool CheckCancelOrSuspend()
        {
            var st = _instance.GetStatus();
            if (st == EdmTaskStatus.EdmTaskStat_CancelPending)
            { _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneCancelled, 0, null, Type.Missing); return true; }
            if (st == EdmTaskStatus.EdmTaskStat_SuspensionPending)
            {
                _instance.SetStatus(EdmTaskStatus.EdmTaskStat_Suspended, 0, null, Type.Missing);
                while (_instance.GetStatus() == EdmTaskStatus.EdmTaskStat_Suspended) System.Threading.Thread.Sleep(1000);
                if (_instance.GetStatus() == EdmTaskStatus.EdmTaskStat_ResumePending)
                    _instance.SetStatus(EdmTaskStatus.EdmTaskStat_Running, 0, null, Type.Missing);
            }
            return false;
        }

        private bool ReadSettings()
        {
            _bat = Str(_instance.GetValEx(Cfg.BatPath), "");
            _script = Str(_instance.GetValEx(Cfg.Script), "");
            _acc = Str(_instance.GetValEx(Cfg.AccoreCon), "");
            _gs = Str(_instance.GetValEx(Cfg.Ghostscript), "");
            _outTpl = RepairMojibake(Str(_instance.GetValEx(Cfg.OutputPath), Cfg.DefOutput));
            _prefix = RepairMojibake(Str(_instance.GetValEx(Cfg.Prefix), Cfg.DefPrefix));
            _suffix = RepairMojibake(Str(_instance.GetValEx(Cfg.Suffix), Cfg.DefSuffix));
            _errLog = Str(_instance.GetValEx(Cfg.ErrorLog), "");
            _statusVar = Str(_instance.GetValEx(Cfg.StatusVar), Cfg.DefStatusVar);
            _transition = RepairMojibake(Str(_instance.GetValEx(Cfg.Transition), Cfg.DefTransition));
            int t; if (!int.TryParse(Str(_instance.GetValEx(Cfg.TimeoutMin), Cfg.DefTimeout.ToString()), out t)) t = Cfg.DefTimeout;
            _timeoutMin = Math.Max(1, Math.Min(28, t));
            _linkPdfToSource = Str(_instance.GetValEx(Cfg.LinkRefs), "1") != "0";

            // Приоритет: код сценария в задаче > явный путь к bat > движок, встроенный в DLL.
            if (!string.IsNullOrWhiteSpace(_script)) return true;
            if (string.IsNullOrWhiteSpace(_bat))
            {
                string engDir;
                _bat = EngineExtractor.EnsureEngine(out engDir);
            }
            return !string.IsNullOrWhiteSpace(_bat) && File.Exists(_bat);
        }

        // (Поиск dwg2pdf.bat рядом с DLL убран: движок встроен в сборку как ресурс.)

        private void Fail(string msg)
        {
            Logger.Log(msg, Logger.LogLevel.Error);
            try { _instance.SetStatus(EdmTaskStatus.EdmTaskStat_DoneFailed, 0, msg, Type.Missing); } catch { }
        }


        // Конвертация одного файла: запуск bat в папке DWG + перенос итогового PDF во временную папку.
        private void ConvertOne(FileItem f, string temp)
        {
            f.Success = false;
            try { ConvertAndValidate(f, temp); }
            catch (Exception ex)
            {
                f.Success = false;
                f.Error = "PDF не допущен к загрузке: " + ex.Message;
                Logger.Log(f.SourcePath + ": " + f.Error, Logger.LogLevel.Error);
                try
                {
                    SourcePdfStatus.MarkRejected(_vault, f.SourceFileId, f.SourceFolderId, _statusVar);
                    f.Error += " В карточку исходника записано " + _statusVar + "=0.";
                }
                catch (Exception cardError)
                {
                    f.Error += " НЕ УДАЛОСЬ записать " + _statusVar + "=0: " + cardError.Message;
                    Logger.Log(f.SourcePath + ": " + f.Error, Logger.LogLevel.Error);
                }
            }
        }

        private void ConvertAndValidate(FileItem f, string temp)
        {
            var elapsed = Stopwatch.StartNew();
            string dir = Path.GetDirectoryName(f.SourcePath);
            string baseName = Path.GetFileNameWithoutExtension(f.SourcePath);
            string mono = Path.Combine(dir, baseName + "~mono.tmp.pdf");
            string color = Path.Combine(dir, baseName + ".pdf");
            string perFile = Path.Combine(temp, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(perFile);
            string ghostscript = PdfValidator.ResolveGhostscript(_gs);
            string recovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DWGToBundleTaskAddIn", "pdf-recovery");
            try
            {
                // Never accept a result left by an earlier conversion.
                if (File.Exists(mono)) { File.SetAttributes(mono, FileAttributes.Normal); File.Delete(mono); }
                int code;
                string log;
                using (new LocalPdfGuard(color, recovery))
                    code = RunBat(f.SourcePath, dir, out log);
                Logger.Log(string.Format("bat exit={0} file={1}", code, Path.GetFileName(f.SourcePath)),
                    code == 0 ? Logger.LogLevel.Info : Logger.LogLevel.Warning);
                if (!string.IsNullOrEmpty(log)) Logger.Log("bat out: " + log.Trim());
                if (code != 0) throw new InvalidDataException("dwg2pdf.bat: код выхода " + code + ".");
                int remaining = (int)Math.Max(0, _timeoutMin * 60000L - elapsed.ElapsedMilliseconds);
                int pages = PdfValidator.Validate(mono, ghostscript, perFile, remaining);
                Logger.Log("PDF проверен: " + f.SourcePath + ", непустых страниц: " + pages);
                f.TempPdf = Path.Combine(perFile, Path.GetFileName(f.OutputPath));
                File.Copy(mono, f.TempPdf, false);
                f.Success = true; // the only path allowed into AddFiles
            }
            finally
            {
                try { if (File.Exists(mono)) { File.SetAttributes(mono, FileAttributes.Normal); File.Delete(mono); } }
                catch (Exception ex) { Logger.Log("cleanup mono: " + ex.Message, Logger.LogLevel.Warning); }
            }
        }

        private int RunBat(string dwg, string workDir, out string output)
        {
            var sb = new StringBuilder();
            var args = new StringBuilder();
            args.Append("/d /c \"\"").Append(_bat).Append("\" \"").Append(dwg).Append('"');
            if (!string.IsNullOrWhiteSpace(_acc) || !string.IsNullOrWhiteSpace(_gs)) args.Append(" \"").Append(_acc ?? "").Append('"');
            if (!string.IsNullOrWhiteSpace(_gs)) args.Append(" \"").Append(_gs).Append('"');
            args.Append('"');

            var psi = new ProcessStartInfo("cmd.exe", args.ToString())
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["SLD_HEADLESS"] = "1";
            if (!string.IsNullOrWhiteSpace(_acc)) psi.EnvironmentVariables["DWG2PDF_ACCORECONSOLE"] = _acc;
            if (!string.IsNullOrWhiteSpace(_gs)) psi.EnvironmentVariables["DWG2PDF_GS"] = _gs;

            int exit = -1;
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); } };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(_timeoutMin * 60 * 1000))
                {
                    try { Process.Start("taskkill", "/T /F /PID " + p.Id).WaitForExit(5000); } catch { }
                    try { p.Kill(); } catch { }
                    lock (sb) output = sb.ToString();
                    Logger.Log("bat timeout po tajmautu " + _timeoutMin + " min", Logger.LogLevel.Error);
                    return 124;
                }
                p.WaitForExit(); // дождаться обработчиков stdout/stderr
                exit = p.ExitCode;
            }
            lock (sb) output = sb.ToString();
            return exit;
        }


        // Разбор шаблона пути результата: <SourceFolderPath> <FileName> <FileRev> <FileVer>
        // <FileState> <RootFolder> <Date> и <%CARSOJKA%> (переменная карточки).
        private string ParseOutputPath(int fileId, int folderId, string path)
        {
            var file = _vault.GetObject(EdmObjectType.EdmObject_File, fileId) as IEdmFile5;
            var fi = new FileInfo(file.GetLocalPath(folderId));
            var map = new Dictionary<string, string>
            {
                { "<FileExt>", fi.Extension.TrimStart('.') },
                { "<FileName>", Path.GetFileNameWithoutExtension(fi.FullName) },
                { "<Prefix>", _prefix ?? "" },
                { "<Suffix>", _suffix ?? "" },
                { "<FileRev>", file.CurrentRevision },
                { "<FileVer>", file.CurrentVersion.ToString() },
                { "<FileState>", file.CurrentState != null ? file.CurrentState.Name : "" },
                { "<SourceFolderPath>", fi.DirectoryName },
                { "<RootFolder>", _vault.RootFolderPath },
                { "<Date>", DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }
            };
            foreach (var kv in map) path = path.Replace(kv.Key, kv.Value);

            var ev = file.GetEnumeratorVariable() as IEdmEnumeratorVariable8;
            if (ev != null)
            {
                for (int idx = path.IndexOf("<%"); idx != -1; idx = path.IndexOf("<%"))
                {
                    int end = path.IndexOf("%>", idx);
                    if (end < 0) break;
                    string name = path.Substring(idx + 2, end - idx - 2);
                    object val = null;
                    try
                    {
                        ev.GetVar(name, "", out val);
                        if (val == null) ev.GetVar(name, "Model", out val);
                    }
                    catch { }
                    path = path.Replace("<%" + name + "%>", val == null ? "" : val.ToString());
                }
                try { ev.CloseFile(false); } catch { }
            }
            // Трактовка пути результата:
            //  - абсолютный (диск C:\ или UNC \\server\...) — используем как есть;
            //  - относительный (например \Выход\PDF\<FileName>) — от корня ХРАНИЛИЩА
            //    на этой машине (IEdmVault.RootFolderPath).
            return ResolveAgainstVault(path) ;
        }

        // Относительный путь -> абсолютный в локальном корне хранилища.
        private string ResolveAgainstVault(string rel)
        {
            bool rooted = rel.Length >= 2 &&
                ((rel[1] == ':' && (rel[0] >= 'A' && rel[0] <= 'Z' || rel[0] >= 'a' && rel[0] <= 'z'))
                 || rel.StartsWith(@"\\"));
            if (!rooted)
            {
                string trimmed = rel.TrimStart('\\', '/');
                rel = string.IsNullOrEmpty(trimmed)
                    ? _vault.RootFolderPath
                    : Path.Combine(_vault.RootFolderPath, trimmed);
            }
            return Path.GetFullPath(rel + ".pdf");
        }

        private bool IsFolderInVault(string path)
        {
            string root = Path.GetFullPath(_vault.RootFolderPath).TrimEnd('\\', '/');
            string full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private void WriteErrorLog(List<FileItem> files)
        {
            if (string.IsNullOrEmpty(_errLog)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("DWG->PDF bundle. " + DateTime.Now);
                foreach (var f in files)
                    sb.AppendLine(string.Format("{0}\t{1}\t{2}", f.Success ? "OK" : "FAIL", f.SourcePath, f.Success ? f.OutputPath : f.Error));
                var folder = _vault.GetFolderFromPath(_errLog);
                if (folder == null) { Logger.Log("Papka zhurnala ne v arhive: " + _errLog, Logger.LogLevel.Warning); return; }
                string local = Path.Combine(Path.GetTempPath(), "DWG2PDM_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(local, sb.ToString());
                var ba = _vault.CreateUtility(EdmUtility.EdmUtil_BatchAdd) as IEdmBatchAdd;
                ba.AddFileFromPath(local, folder.ID);
                EdmFileInfo[] res = null;
                ba.CommitAdd(0, ref res, 0, new EdmCallbackImpl());
                try { File.Delete(local); } catch { }
            }
            catch (Exception ex) { Logger.Log("WriteErrorLog: " + ex, Logger.LogLevel.Error); }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }


        // ---- Ручной запуск из контекстного меню (без экземпляра задачи) ----
        // Настройки берутся по умолчанию (движок встроен в DLL, DefOutput/DefPrefix),
        // т.к. у командного запуска нет IEdmTaskInstance с настройками конкретной задачи.
        public void OnMenuRun(ref EdmCmd poCmd, ref EdmCmdData[] ppoData)
        {
            _instance = null;
            string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "DWGToBundleTaskAddIn", "menu-" + Guid.NewGuid().ToString("N"));
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                if (!ReadManualSettings())
                {
                    Msg(poCmd, "Не удалось подготовить движок (нет встроенного dwg2pdf.bat).", true);
                    return;
                }
                Directory.CreateDirectory(temp);
                Logger.Log(string.Format("MenuRun start. bat={0} | tpl={1} | prefix='{2}'", _bat, _outTpl, _prefix));

                var files = CollectMenu(ppoData);
                if (files.Count == 0)
                {
                    Msg(poCmd, "Нет DWG/DXF файлов для обработки.", false);
                    return;
                }
                foreach (var f in files)
                {
                    ProcessOne(poCmd, f, temp);
                    System.Windows.Forms.Application.DoEvents();
                }

                int ok = 0; foreach (var f in files) if (f.Success) ok++;
                var sb = new StringBuilder();
                sb.AppendFormat("DWG -> PDF готово: {0} из {1}.", ok, files.Count);
                foreach (var f in files) if (!f.Success) sb.Append("\n").Append(Path.GetFileName(f.SourcePath)).Append(": ").Append(f.Error);
                Msg(poCmd, sb.ToString(), ok < files.Count);
                Logger.Log("MenuRun done: ok=" + ok + "/" + files.Count);
            }
            catch (Exception ex)
            {
                Logger.Log("MenuRun: " + ex, Logger.LogLevel.Error);
                Msg(poCmd, "Ошибка ручного запуска: " + ex.Message, true);
            }
            finally
            {
                Cursor.Current = Cursors.Default;
                TryDeleteDir(temp);
            }
        }

        private bool ReadManualSettings()
        {
            _bat = ""; _script = ""; _acc = ""; _gs = ""; _errLog = "";
            _outTpl = Cfg.DefOutput; _prefix = Cfg.DefPrefix; _suffix = Cfg.DefSuffix;
            _statusVar = Cfg.DefStatusVar;
            _transition = Cfg.DefTransition;
            _timeoutMin = Cfg.DefTimeout;
            _linkPdfToSource = true;
            string engDir;
            _bat = EngineExtractor.EnsureEngine(out engDir);
            return !string.IsNullOrWhiteSpace(_bat) && File.Exists(_bat);
        }

        private List<FileItem> CollectMenu(EdmCmdData[] data)
        {
            var list = new List<FileItem>();
            if (data == null) return list;
            foreach (var d in data)
            {
                int id = d.mlObjectID1;
                IEdmFile5 file = null;
                if (id > 0)
                    file = _vault.GetObject(EdmObjectType.EdmObject_File, id) as IEdmFile5;
                if (file == null && !string.IsNullOrWhiteSpace(d.mbsStrData1))
                {
                    // Кнопка на карточке может передать имя вместо ID — резолвим по пути.
                    IEdmFolder5 byPathFolder;
                    file = _vault.GetFileFromPath(d.mbsStrData1, out byPathFolder) as IEdmFile5;
                }
                if (file == null) continue;
                var ext = Path.GetExtension(file.Name);
                bool okExt = false;
                foreach (var a in Accepted) if (string.Equals(a, ext, StringComparison.OrdinalIgnoreCase)) { okExt = true; break; }
                if (!okExt) { Logger.Log("Propusk (ne DWG/DXF): " + file.Name, Logger.LogLevel.Info); continue; }
                IEdmPos5 pos = file.GetFirstFolderPosition();
                var fld = file.GetNextFolder(pos) as IEdmFolder5;
                if (fld == null) continue;
                int folderId = fld.ID;
                list.Add(new FileItem { SourceFileId = id, SourceFolderId = folderId, SourcePath = file.GetLocalPath(folderId) });
            }
            return list;
        }

        private static void Msg(EdmCmd poCmd, string text, bool error)
        {
            try
            {
                IWin32Window owner = poCmd.mlParentWnd != 0 ? new Win32Owner(new IntPtr(poCmd.mlParentWnd)) : null;
                MessageBox.Show(owner, text, "DWG -> PDF",
                    MessageBoxButtons.OK, error ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch { MessageBox.Show(text, "DWG -> PDF"); }
        }

        private sealed class Win32Owner : IWin32Window
        {
            private readonly IntPtr _h;
            public Win32Owner(IntPtr h) { _h = h; }
            public IntPtr Handle { get { return _h; } }
        }


        // Штатная связь PDM «Связанные файлы»: опубликованный PDF (РОДИТЕЛЬ) -> исходный DWG (ПОТОМОК).
        // PDF остаётся отдельным файлом в архиве — здесь на него навешивается ссылка на исходник,
        // чтобы в дереве связанных файлов PDF отображался исходный чертёж.
        // Идемпотентно: если связь уже есть (повторный запуск задачи), дубль не создаётся.
        // Ошибку связи не считаем ошибкой публикации: корректный PDF уже в архиве.
        private void LinkPdfToSource(int sourceFileId, string pdfPath)
        {
            IEdmFolder5 pdfFolder;
            var pdf = _vault.GetFileFromPath(pdfPath, out pdfFolder) as IEdmFile5;
            if (pdf == null)
            {
                Logger.Log("Связь не создана: PDF не найден в архиве по пути: " + pdfPath, Logger.LogLevel.Warning);
                return;
            }
            int pdfId = pdf.ID;
            int pdfFolderId = pdfFolder != null ? pdfFolder.ID : 0;

            // Исходный DWG — потомок связи; определяем его и папку для корректной ссылки.
            var dwg = _vault.GetObject(EdmObjectType.EdmObject_File, sourceFileId) as IEdmFile5;
            if (dwg == null)
            {
                Logger.Log("Связь не создана: исходный DWG не найден в архиве по ID=" + sourceFileId, Logger.LogLevel.Warning);
                return;
            }
            int sourceFolderId = 0;
            try { sourceFolderId = dwg.GetNextFolder(dwg.GetFirstFolderPosition()).ID; }
            catch { sourceFolderId = 0; }
            if (sourceFolderId == 0)
            {
                Logger.Log("Связь не создана: не определена папка исходного DWG ID=" + sourceFileId, Logger.LogLevel.Warning);
                return;
            }

            // Родитель — PDF: проверяем дерево ссылок PDF на уже существующего потомка-DWG.
            if (AlreadyLinked(pdfId, sourceFileId))
            {
                Logger.Log("Связь PDF->DWG уже существует, дубль не создаётся. PDF ID=" + pdfId);
                return;
            }

            var addRefs = _vault.CreateUtility(EdmUtility.EdmUtil_AddCustomRefs) as IEdmAddCustomRefs;
            if (addRefs == null)
            {
                Logger.Log("Связь не создана: утилита EdmUtil_AddCustomRefs недоступна.", Logger.LogLevel.Warning);
                return;
            }

            // Структуру ссылок «Связанные файлы» PDM позволяет менять только на снятом с
            // регистрации (checked out) файле-родителе. Родителем делаем PDF: снимаем с
            // регистрации PDF, добавляем ссылку на исходный DWG и сразу регистрируем обратно.
            if (pdf.IsLocked)
            {
                // PDF уже кем-то снят с регистрации — лезть в его ссылки нельзя.
                Logger.Log("Связь не создана: PDF ID=" + pdfId +
                    " уже снят с регистрации (checkout другим пользователем).", Logger.LogLevel.Warning);
                return;
            }

            bool lockedByUs = false;
            try
            {
                pdf.LockFile(pdfFolderId, 0); // exclusive checkout родителя-PDF
                lockedByUs = true;

                var refs = new[] { new EdmSelItem { mlDocID = sourceFileId, mlProjID = sourceFolderId } };
                addRefs.AddReferencesID(pdfId, ref refs);
                if (!addRefs.CreateTree((int)EdmCreateReferenceFlags.Ecrf_Nothing))
                    throw new COMException("EdmAddCustomRefs.CreateTree вернул false.");
                if (!addRefs.CreateReferences())
                    throw new COMException("EdmAddCustomRefs.CreateReferences вернул false.");

                // Фиксируем изменённую структуру ссылок обратной регистрацией PDF-родителя.
                pdf.UnlockFile(0, "DWG->PDF: связь «Связанные файлы» (PDF -> исходный DWG)", 0, null);
                lockedByUs = false; // регистрация выполнена — откат не нужен

                Logger.Log("Создана связь «Связанные файлы»: PDF ID=" + pdfId + " -> DWG ID=" + sourceFileId);
            }
            finally
            {
                // Если после checkout что-то сорвалось — не оставляем PDF «на регистрации».
                if (lockedByUs)
                {
                    try
                    {
                        var recheck = _vault.GetObject(EdmObjectType.EdmObject_File, pdfId) as IEdmFile5;
                        if (recheck != null && recheck.IsLocked) recheck.UndoLockFile(0, false);
                    }
                    catch { }
                }
            }
        }

        // Проверка, что у файла-родителя уже есть дочерняя ссылка на childFileId,
        // чтобы не плодить дубли при повторном запуске задачи.
        private bool AlreadyLinked(int parentFileId, int childFileId)
        {
            try
            {
                var parent = _vault.GetObject(EdmObjectType.EdmObject_File, parentFileId) as IEdmFile5;
                if (parent == null) return false;
                var tree = parent.GetReferenceTree(parent.GetNextFolder(parent.GetFirstFolderPosition()).ID, 0);
                if (tree == null) return false;
                string projectName = null;
                IEdmPos5 pos = tree.GetFirstChildPosition(ref projectName, false, false, 0);
                while (pos != null && !pos.IsNull)
                {
                    var child = tree.GetNextChild(pos);
                    if (child != null && child.FileID == childFileId) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("AlreadyLinked: " + ex.Message, Logger.LogLevel.Warning);
            }
            return false;
        }

        // Conversion, validation and archive publication share one lease. A rejected
        // PDF never reaches PDM; an archive error never counts as a successful task.
        private void ProcessOne(EdmCmd command, FileItem f, string temp)
        {
            try
            {
                f.OutputPath = ParseOutputPath(f.SourceFileId, f.SourceFolderId, _outTpl);
                string colorPath = Path.Combine(Path.GetDirectoryName(f.SourcePath),
                    Path.GetFileNameWithoutExtension(f.SourcePath) + ".pdf");
                using (ConversionLease.Acquire(colorPath, f.OutputPath))
                {
                    GetLatestVersions(command, new List<FileItem> { f });
                    ConvertOne(f, temp);
                    if (!f.Success) return;
                    try
                    {
                        if (IsFolderInVault(f.OutputPath))
                            PdfPublication.Save(new PdmPdfArchive(_vault), f.OutputPath, f.TempPdf, temp,
                                path => PdfValidator.Validate(path, PdfValidator.ResolveGhostscript(_gs), temp, _timeoutMin * 60000));
                        else
                            PdfPublication.SaveOutsideVault(f.OutputPath, f.TempPdf);
                        Logger.Log("PDF проверен и сохранён: " + f.OutputPath);
                        // Штатная связь PDM «Связанные файлы»: PDF -> исходный DWG. Выполняется только для
                        // PDF внутри хранилища и не отменяет уже корректную публикацию при любой ошибке связи.
                        if (_linkPdfToSource && IsFolderInVault(f.OutputPath))
                        {
                            try { LinkPdfToSource(f.SourceFileId, f.OutputPath); }
                            catch (Exception linkError)
                            {
                                Logger.Log("PDF опубликован, но связь «Связанные файлы» с DWG не создана: " +
                                    linkError.Message, Logger.LogLevel.Warning);
                            }
                        }
                        // Корректный PDF опубликован -> помечаем карточку исходника переменной-признаком = 1.
                        try
                        {
                            SourcePdfStatus.MarkAccepted(_vault, f.SourceFileId, f.SourceFolderId, _statusVar);
                        }
                        catch (Exception cardError)
                        {
                            f.Success = false;
                            f.Error = "PDF опубликован, но НЕ УДАЛОСЬ записать " + _statusVar + "=1: " + cardError.Message;
                            Logger.Log(f.SourcePath + ": " + f.Error, Logger.LogLevel.Error);
                        }
                        // Сам выполняем переход рабочего процесса над исходником сразу после публикации PDF:
                        // автопереход в потоке по pdf=1 срабатывает раньше создания PDF и ненадёжен.
                        if (f.Success && !string.IsNullOrWhiteSpace(_transition))
                        {
                            try
                            {
                                string info = SourceWorkflowTransition.Perform(_vault, f.SourceFileId, f.SourceFolderId,
                                    _transition, "DWG->PDF: корректный PDF опубликован");
                                Logger.Log(f.SourcePath + ": " + info);
                            }
                            catch (Exception trError)
                            {
                                Logger.Log("Переход «" + _transition + "» для " + f.SourcePath +
                                    " не выполнен: " + trError.Message, Logger.LogLevel.Warning);
                            }
                        }
                        else if (f.Success && string.IsNullOrWhiteSpace(_transition))
                        {
                            Logger.Log(f.SourcePath + ": переход не выполняется — поле «Переход после корректного PDF» в настройках задачи пусто.",
                                Logger.LogLevel.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        f.Success = false;
                        f.Error = "PDF проверен, но сохранение не подтверждено: " + ex.Message;
                        Logger.Log("Publish " + f.OutputPath + ": " + ex, Logger.LogLevel.Error);
                        // Корректного PDF в архиве нет -> признак = 0.
                        try
                        {
                            SourcePdfStatus.MarkRejected(_vault, f.SourceFileId, f.SourceFolderId, _statusVar);
                            f.Error += " В карточку исходника записано " + _statusVar + "=0.";
                        }
                        catch (Exception cardError)
                        {
                            f.Error += " НЕ УДАЛОСЬ записать " + _statusVar + "=0: " + cardError.Message;
                            Logger.Log(f.SourcePath + ": " + f.Error, Logger.LogLevel.Error);
                        }
                    }
                }
            }
            catch (ConversionBusyException ex)
            {
                f.Success = false;
                f.Error = ex.Message; // do not set pdf=0 while another conversion is running
                Logger.Log("Busy " + f.SourcePath + ": " + ex.Message, Logger.LogLevel.Warning);
            }
            catch (Exception ex)
            {
                f.Success = false;
                f.Error = ex.Message;
                Logger.Log("Process " + f.SourcePath + ": " + ex, Logger.LogLevel.Error);
            }
        }

    }
}
