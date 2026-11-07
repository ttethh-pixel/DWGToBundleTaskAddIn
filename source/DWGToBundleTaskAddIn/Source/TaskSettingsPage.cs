using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    // Единые имена переменных задачи и значения по умолчанию.
    internal static class Cfg
    {
        public const string Script      = "DWG2PDM_Script";   // тело .bat прямо в задаче (необяз.)
        public const string BatPath     = "DWG2PDM_BatPath";  // необязательный override пути к bat
        public const string AccoreCon   = "DWG2PDM_Accoreconsole";
        public const string Ghostscript = "DWG2PDM_Ghostscript";
        public const string OutputPath  = "DWG2PDM_OutputPath";
        public const string Prefix      = "DWG2PDM_Prefix";    // префикс имени результата (не зашит)
        public const string Suffix      = "DWG2PDM_Suffix";    // суффикс имени результата (вставляется токеном <Suffix>)
        public const string TimeoutMin  = "DWG2PDM_TimeoutMin";
        public const string ReplaceDupe = "DWG2PDM_ReplaceDupe";
        public const string ErrorLog    = "DWG2PDM_ErrorLogPath";
        public const string StatusVar   = "DWG2PDM_StatusVar";   // имя версии-независимой переменной-признака PDF
        public const string Transition  = "DWG2PDM_Transition";  // имя перехода, выполняемого после корректного PDF
        public const string LinkRefs    = "DWG2PDM_LinkRefs";    // "1" — связывать PDF с исходным DWG (Связанные файлы)

        // Префикс вставляется в шаблон токеном <Prefix>; по умолчанию пуст — имя = имя чертежа.
        // Суффикс вставляется токеном <Suffix> сразу после имени, перед расширением .pdf.
        public const string DefPrefix   = "";
        // Суффикс по умолчанию — «новый»: имя результата = <имя чертежа>новый.pdf.
        public const string DefSuffix   = "новый";
        // Путь по умолчанию — в хранилище PDM в папке \Архив\Подлинники\Входящие\ (относительно корня хранилища).
        public const string DefOutput   = @"\Архив\Подлинники\Входящие\<Prefix><FileName><Suffix>";
        public const int    DefTimeout  = 25;
        // Переменная-признак в карточке исходника: "1" = корректный PDF опубликован, "0" = корректного PDF нет.
        public const string DefStatusVar = "pdf";
        // Имя перехода рабочего процесса, который админ сам выполняет над исходным DWG/DXF
        // СРАЗУ после подтверждения публикации PDF. Это заменяет ненадёжный автопереход в потоке
        // по pdf=1 (поток срабатывает раньше, чем PDF физически создан). Пусто — переход не выполнять.
        public const string DefTransition = "pdf ок";
    }

    // Страница настроек задачи. EPDM берёт Handle (EdmTaskSetupPage.mlPageHwnd)
    // и вызывает LoadData/StoreData по IDispatch.
    [ComVisible(true)]
    public class TaskSettingsPage : UserControl
    {
        private TextBox txtScript, txtBat, txtAcc, txtGs, txtOut, txtPrefix, txtSuffix, txtLog, txtStatusVar, txtTransition;
        private NumericUpDown numTimeout;
        private CheckBox chkLink;

        public TaskSettingsPage()
        {
            Name = "DWG -> PDF (bundle)";
            Font = System.Drawing.SystemFonts.MessageBoxFont;
            Size = new System.Drawing.Size(520, 470);

            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 13,
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int r = 0; r < 13; r++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles[0] = new RowStyle(SizeType.Absolute, 120); // строка кода сценария

            // 0 — необязательный код сценария (.bat), вставляется прямо в задачу.
            layout.Controls.Add(new Label { Text = "&Код сценария (.bat), необяз.:", Dock = DockStyle.Fill, AutoSize = true }, 0, 0);
            txtScript = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                WordWrap = false,
                Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericMonospace, 8.25f),
                AccessibleDescription = "Если заполнено — аддин выполнит этот код как .bat. Пусто — движок, встроенный в DLL, или путь ниже."
            };
            layout.Controls.Add(txtScript, 1, 0);

            txtBat = AddRow(layout, 1, "Путь к dwg2pdf.bat (необяз.):", "Пусто — движок встроен в DLL (распаковывается в %AppData%\\DWGToBundleTaskAddIn\\engine\\<MVID>)");
            txtAcc = AddRow(layout, 2, "accoreconsole.exe (необяз.):", "Пусто — из PATH / env / значения bat");
            txtGs  = AddRow(layout, 3, "gswin64c.exe (необяз.):", "Пусто — из PATH / env / значения bat");
            txtOut = AddRow(layout, 4, "&Путь результата PDF (от корня хранилища):", "\\PDF\\<Prefix><FileName> — от корня хранилища; или абсолютный путь; токены <SourceFolderPath> <Prefix> <FileName> <FileRev> <%VAR%>");
            txtPrefix = AddRow(layout, 5, "Префикс имени результата (<Prefix>):", "Вставляется в шаблон токеном <Prefix> перед именем. Пусто — имя равно имени чертежа. Пример: «новый», «REV_».");
            txtSuffix = AddRow(layout, 12, "Суффикс имени результата (<Suffix>):", "Вставляется в шаблон токеном <Suffix> сразу после имени, перед расширением .pdf. По умолчанию «новый»: <имя чертежа>новый.pdf.");
            numTimeout = new NumericUpDown { Minimum = 1, Maximum = 28, Value = Cfg.DefTimeout, Dock = DockStyle.Fill };
            AddCell(layout, 6, "Таймаут, мин (<= 28):", numTimeout);
            AddCell(layout, 7, "", new Label { Text = "Существующий PDF обновляется новой версией без удаления из PDM.", Dock = DockStyle.Fill, AutoSize = true });
            txtLog = AddRow(layout, 8, "Папка журнала в архиве (необяз.):", "Например \\\\server\\vault\\Logs\\DWG2PDF");
            txtStatusVar = AddRow(layout, 9, "Переменная‑признак PDF (карточка исходника):",
                "Имя версии‑независимой переменной в карточке DWG/DXF. Задаёт «1» при корректном PDF и «0», когда корректного PDF нет. По умолчанию: pdf.");
            txtTransition = AddRow(layout, 10, "Переход после корректного PDF (имя перехода):",
                "Админ сам выполняет этот переход над исходным DWG/DXF сразу после подтверждения публикации PDF (заменяет ненадёжный автопереход в потоке по pdf). Пусто — не выполнять. По умолчанию: pdf ок.");

            chkLink = new CheckBox
            {
                Text = "Связывать PDF с исходным DWG (Связанные файлы)",
                Checked = true,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AccessibleDescription = "После публикации PDF как отдельного файла добавить штатную связь PDM «Связанные файлы»: PDF -> исходный DWG. Снятие — PDF грузится без связи."
            };
            AddCell(layout, 11, "", chkLink);

            var hint = new Label
            {
                Text = "Порядок выполнения зашит. Движок: accoreconsole -> цветной PDF -> Ghostscript settransfer -> ч/б PDF.\r\n" +
                       "Путь результата: относительный задаётся ОТ КОРНЯ ХРАНИЛИЩА (например \\PDF\\<Prefix><FileName>), абсолютный — как есть.\r\n" +
                       "Префикс имени задаётся полем «Префикс» и подставляется токеном <Prefix>.\r\n" +
                       "Ручной запуск — пункт контекстного меню «Конвертировать DWG в PDF» (использует значения по умолчанию).\r\n" +
                       "Приоритет: код сценария в задаче > явный путь к bat > движок, встроенный в DLL.",
                Dock = DockStyle.Fill, AutoSize = true, ForeColor = System.Drawing.Color.DimGray
            };
            AddCell(layout, 11, "", hint);
            Controls.Add(layout);
        }

        private TextBox AddRow(TableLayoutPanel p, int row, string label, string tip)
        {
            p.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, AutoSize = true }, 0, row);
            var tb = new TextBox { Dock = DockStyle.Fill };
            if (!string.IsNullOrEmpty(tip)) tb.AccessibleDescription = tip;
            p.Controls.Add(tb, 1, row);
            return tb;
        }

        private void AddCell(TableLayoutPanel p, int row, string label, Control c)
        {
            if (!string.IsNullOrEmpty(label))
                p.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, AutoSize = true }, 0, row);
            p.Controls.Add(c, 1, row);
        }

        // --- контракт страницы настроек EPDM (вызывается по IDispatch) ---
        public void LoadData(ref EdmCmd poCmd)
        {
            try
            {
                var tp = poCmd.mpoExtra as IEdmTaskProperties;
                if (tp == null) return;
                txtScript.Text = Str(tp.GetValEx(Cfg.Script), "");
                txtBat.Text = Str(tp.GetValEx(Cfg.BatPath), "");
                txtAcc.Text = Str(tp.GetValEx(Cfg.AccoreCon), "");
                txtGs.Text  = Str(tp.GetValEx(Cfg.Ghostscript), "");
                txtOut.Text = Str(tp.GetValEx(Cfg.OutputPath), Cfg.DefOutput);
                txtPrefix.Text = Str(tp.GetValEx(Cfg.Prefix), Cfg.DefPrefix);
                txtSuffix.Text = Str(tp.GetValEx(Cfg.Suffix), Cfg.DefSuffix);
                txtLog.Text = Str(tp.GetValEx(Cfg.ErrorLog), "");
                txtStatusVar.Text = Str(tp.GetValEx(Cfg.StatusVar), Cfg.DefStatusVar);
                txtTransition.Text = Str(tp.GetValEx(Cfg.Transition), Cfg.DefTransition);
                chkLink.Checked = Str(tp.GetValEx(Cfg.LinkRefs), "1") != "0";
                int t;
                if (int.TryParse(Str(tp.GetValEx(Cfg.TimeoutMin), Cfg.DefTimeout.ToString()), out t))
                    numTimeout.Value = Math.Min(numTimeout.Maximum, Math.Max(numTimeout.Minimum, t));
            }
            catch (Exception ex) { Logger.Log("LoadData: " + ex, Logger.LogLevel.Error); }
        }

        public void StoreData(ref EdmCmd poCmd)
        {
            try
            {
                var tp = poCmd.mpoExtra as IEdmTaskProperties;
                if (tp == null) return;
                poCmd.mbCancel = 0;
                tp.SetValEx(Cfg.Script, txtScript.Text);
                tp.SetValEx(Cfg.BatPath, txtBat.Text.Trim());
                tp.SetValEx(Cfg.AccoreCon, txtAcc.Text.Trim());
                tp.SetValEx(Cfg.Ghostscript, txtGs.Text.Trim());
                tp.SetValEx(Cfg.OutputPath, string.IsNullOrWhiteSpace(txtOut.Text) ? Cfg.DefOutput : txtOut.Text.Trim());
                tp.SetValEx(Cfg.Prefix, txtPrefix.Text.Trim());
                tp.SetValEx(Cfg.Suffix, string.IsNullOrWhiteSpace(txtSuffix.Text) ? Cfg.DefSuffix : txtSuffix.Text.Trim());
                tp.SetValEx(Cfg.TimeoutMin, (int)numTimeout.Value);
                tp.SetValEx(Cfg.ErrorLog, txtLog.Text.Trim());
                tp.SetValEx(Cfg.StatusVar, string.IsNullOrWhiteSpace(txtStatusVar.Text) ? Cfg.DefStatusVar : txtStatusVar.Text.Trim());
                tp.SetValEx(Cfg.Transition, txtTransition.Text.Trim());
                tp.SetValEx(Cfg.LinkRefs, chkLink.Checked ? "1" : "0");
            }
            catch (Exception ex) { Logger.Log("StoreData: " + ex, Logger.LogLevel.Error); }
        }

        private static string Str(object o, string def)
        {
            if (o == null || o is System.DBNull) return def;
            var s = o.ToString();
            return string.IsNullOrEmpty(s) ? def : s;
        }

    }
}
