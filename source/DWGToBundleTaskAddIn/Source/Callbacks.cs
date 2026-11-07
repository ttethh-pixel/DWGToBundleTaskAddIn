using EPDM.Interop.epdm;

namespace DWGToBundleTaskAddIn
{
    // Колбэк для IEdmBatchAdd.CommitAdd / BatchGet. EPDM вызывает его для
    // сообщений и разрешения ошибок; в фоне всегда отвечаем OK.
    internal class EdmCallbackImpl : IEdmCallback6
    {
        public EdmMBoxResult MsgBox(int lParentWnd, int lMsgID, string bsMsg, EdmMBoxType eType = EdmMBoxType.EdmMbt_OKOnly)
        {
            Logger.Log("Callback MsgBox: " + bsMsg, Logger.LogLevel.Info);
            return EdmMBoxResult.EdmMbr_OK;
        }

        public void Resolve(int lParentWnd, ref EdmCmdData[] ppoItems)
        {
            if (ppoItems == null) return;
            foreach (var d in ppoItems)
                Logger.Log(string.Format("Resolving: {0} -> {1} (reason {2})",
                    d.mbsStrData1, d.mbsStrData2, d.mlLongData1), Logger.LogLevel.Warning);
        }

        public bool SetProgress(int lBarIndex, int lPos, string bsMsg) { return true; }
        public void SetProgressRange(int lBarIndex, int lMax) { }

        public void SetStatusMessage(int lBarIndex, string bsMessage)
        {
            Logger.Log("Callback Status: " + bsMessage, Logger.LogLevel.Info);
        }
    }

    // Колбэк для BatchUnlock.
    internal class UnlockCallbackImpl : IEdmUnlockOpCallback
    {
        public EdmUnlockOpReply MsgBox(EdmUnlockOpMsg eMsg, int lDocID, int lProjID, string bsPath, ref EdmUnlockErrInfo poError)
        {
            Logger.Log("Unlock MsgBox: " + eMsg, Logger.LogLevel.Info);
            return EdmUnlockOpReply.Euor_OK;
        }
        public void ProgressBegin(EdmProgressType eType, EdmUnlockEvent eEvent, int lSteps) { }
        public void ProgressEnd(EdmProgressType eType) { }
        public bool ProgressStep(EdmProgressType eType, string bsText, int lProgressPos) { return true; }
        public bool ProgressStepEvent(EdmProgressType eType, EdmUnlockEventMsg eText, int lProgressPos) { return true; }
    }
}
