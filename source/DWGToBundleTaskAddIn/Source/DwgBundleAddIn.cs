using System;
using System.IO;
using System.Runtime.InteropServices;
using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    [Guid("EFBE62F2-6ED8-4FA7-9975-49F92FBD6215")]
    [ComVisible(true)]
    public class DwgBundleAddIn : IEdmAddIn5
    {
        private TaskSettingsPage _page;
        private const int MenuCmdId = 9150;

        public void GetAddInInfo(ref EdmAddInInfo poInfo, IEdmVault5 poVault, IEdmCmdMgr5 poCmdMgr)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DWGToBundleTaskAddIn");
                Logger.Init(dir, Logger.LogLevel.Info);
                Logger.Log("GetAddInInfo: vault=" + poVault.Name +
                    " proc=" + System.Diagnostics.Process.GetCurrentProcess().ProcessName);

                poInfo.mbsAddInName = "DWG->PDF Bundle Task Add-in";
                poInfo.mbsCompany = "NPP Istok";
                poInfo.mbsDescription = "Конвертация DWG/DXF в ч/б PDF через bандл dwg2pdf.bat (accoreconsole + Ghostscript)";
                poInfo.mlAddInVersion = 10002;
                poInfo.mlRequiredVersionMajor = 10;
                poInfo.mlRequiredVersionMinor = 0;

                poCmdMgr.AddHook(EdmCmdType.EdmCmd_TaskRun);
                poCmdMgr.AddHook(EdmCmdType.EdmCmd_TaskSetup);
                poCmdMgr.AddHook(EdmCmdType.EdmCmd_TaskSetupButton);
                poCmdMgr.AddHook(EdmCmdType.EdmCmd_TaskDetails); // как в рабочем DWGtoPDFConverterPDM
            }
            catch (Exception ex) { Logger.Log("GetAddInInfo: " + ex, Logger.LogLevel.Error); }
        }

        public void OnCmd(ref EdmCmd poCmd, ref EdmCmdData[] ppoData)
        {
            try
            {
                switch (poCmd.meCmdType)
                {
                    case EdmCmdType.EdmCmd_TaskRun:
                        new BundleTaskExecutor(poCmd.mpoVault as IEdmVault5).OnTaskRun(ref poCmd, ref ppoData);
                        break;
                    case EdmCmdType.EdmCmd_TaskSetup:
                        OnTaskSetup(ref poCmd);
                        break;
                    case EdmCmdType.EdmCmd_TaskSetupButton:
                        OnTaskSetupButton(ref poCmd);
                        break;
                    case EdmCmdType.EdmCmd_TaskDetails:
                        // Своё окно деталей не показываем — достаточно стандартных сообщений
                        // задачи (SetStatus/SetProgress в BundleTaskExecutor).
                        break;
                }
            }
            catch (COMException ex)
            {
                string n, d;
                (poCmd.mpoVault as IEdmVault5).GetErrorString(ex.ErrorCode, out n, out d);
                Logger.Log("OnCmd COM: " + n + " | " + d, Logger.LogLevel.Error);
            }
            catch (Exception ex) { Logger.Log("OnCmd: " + ex, Logger.LogLevel.Error); }
        }

        private void OnTaskSetup(ref EdmCmd poCmd)
        {
            var tp = poCmd.mpoExtra as IEdmTaskProperties;
            if (tp == null) return;
            // Как в рабочем аналоге DWGtoPDFConverterPDM: SupportsInitForm(1)+SupportsInitExec(2)+
            // SupportsDetails(4)+SupportsChangeState(16)=23. БЕЗ SupportsScheduling(8) — он добавляет
            // «Планирование по времени», который здесь не нужен. Пункт «Команда меню» задачи задаётся
            // триггером задачи в ConisioAdmin, а не кодом.
            tp.TaskFlags = 23;
            _page = new TaskSettingsPage();
            _page.CreateControl();
            _page.LoadData(ref poCmd);
            var sp = new EdmTaskSetupPage
            {
                mbsPageName = _page.Name,
                mlPageHwnd = _page.Handle.ToInt32(),
                mpoPageImpl = _page
            };
            tp.SetSetupPages(new[] { sp });
        }

        private void OnTaskSetupButton(ref EdmCmd poCmd)
        {
            if (poCmd.mbsComment != "OK") return;
            if (_page != null) _page.StoreData(ref poCmd);
        }
    }
}
