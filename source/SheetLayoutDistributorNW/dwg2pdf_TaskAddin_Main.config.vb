' =====================================================================
'  DWG/DXF -> ч/б PDF через dwg2pdf.bat (accoreconsole + Ghostscript)
'  ПАРАМЕТРИЗОВАННАЯ версия: пути bat / accoreconsole / Ghostscript
'  берутся НЕ из хардкода, а по приоритету (каждый путь независимо):
'     1) непустая константа CFG_* ниже  (per-task: правится в том же
'        поле сценария задачи, без рекомпиляции DLL)
'     2) переменная окружения машины / учётки, под которой карточка PDM
'     3) значение по умолчанию DEF_*
'
'  Тип задачи = DWGDXFToPDF (только там аддин делает возврат в PDM).
'  Шаблон Output Path задачи:  <SourceFolderPath>\<FileName>новый
' =====================================================================

Imports System
Imports System.IO
Imports System.Diagnostics
Imports Microsoft.VisualBasic

Namespace SolidWorks

Public Class TaskAddin

    ' ---- КОНФИГУРАЦИЯ. Оставьте "" чтобы взять env/по умолчанию ----
    Private Const CFG_BAT As String = ""   ' dwg2pdf.bat
    Private Const CFG_AC  As String = ""   ' accoreconsole.exe
    Private Const CFG_GS  As String = ""   ' gswin32c.exe / gswin64c.exe
    ' ----------------------------------------------------------------

    ' Имена переменных окружения (2-й приоритет)
    Private Const ENV_BAT As String = "DWG2PDF_BAT"
    Private Const ENV_AC  As String = "DWG2PDF_ACCORECONSOLE"
    Private Const ENV_GS  As String = "DWG2PDF_GS"

    ' Значения по умолчанию (3-й приоритет)
    Private Const DEF_BAT As String = "D:\progs\SheetLayoutDistributorNW\dwg2pdf.bat"
    Private Const DEF_AC  As String = "D:\Program Files\AutoCAD\AutoCAD 2020\accoreconsole.exe"
    Private Const DEF_GS  As String = "C:\Program Files\PDFCreator\Ghostscript\bin\gswin32c.exe"

    Private Function Pick(inlineVal As String, envName As String, defVal As String) As String
        If Not String.IsNullOrEmpty(inlineVal) Then
            If inlineVal.Trim() <> "" Then Return inlineVal.Trim()
        End If
        Dim e As String = Environment.GetEnvironmentVariable(envName)
        If Not String.IsNullOrEmpty(e) Then
            If e.Trim() <> "" Then Return e.Trim()
        End If
        Return defVal
    End Function

    Public Sub Log(message)
        Try
            Dim errorLogFolder As Object = "[TempErrorLogPath]"
            Dim errorLogPath As Object = Path.Combine(errorLogFolder, "<TaskInstanceGuid>.log")
            Dim objWriter As New System.IO.StreamWriter(errorLogPath, True)
            objWriter.WriteLine("[" + Date.Now.ToString("dd.MM.yyyy HH:mm:ss.fff") + "]" + message)
            objWriter.Close()
        Catch
        End Try
    End Sub

    Public Function Main(paramarray prmParameters() as object) As Boolean

        Dim batPath As String = Pick(CFG_BAT, ENV_BAT, DEF_BAT)
        Dim acPath  As String = Pick(CFG_AC, ENV_AC, DEF_AC)
        Dim gsPath  As String = Pick(CFG_GS, ENV_GS, DEF_GS)
        Dim dwgPath As String = Path.Combine("<SourceFolderPath>", "<SourceFileName>")
        Dim target  As String = "[OutputPath]"
        Dim secTarget As String = "[Sec_OutputPath]"

        Try
            Log("Config bat=" + batPath + " | ac=" + acPath + " | gs=" + gsPath)

            If Not File.Exists(batPath) Then
                Log("dwg2pdf.bat not found: " + batPath + " (задайте CFG_BAT / env " + ENV_BAT + ")")
                Return False
            End If
            If Not File.Exists(acPath) Then
                Log("accoreconsole not found: " + acPath + " (задайте CFG_AC / env " + ENV_AC + ")")
                Return False
            End If
            If Not File.Exists(gsPath) Then
                Log("Ghostscript not found: " + gsPath + " (задайте CFG_GS / env " + ENV_GS + ")")
                Return False
            End If
            If Not File.Exists(dwgPath) Then
                Log("DWG not found: " + dwgPath)
                Return False
            End If

            Dim dwgDir As String = Path.GetDirectoryName(dwgPath)
            Dim base As String = Path.GetFileNameWithoutExtension(dwgPath)
            Dim produced As String = Path.Combine(dwgDir, base + "новый.pdf")

            If File.Exists(produced) Then File.Delete(produced)

            Dim q As String = Chr(34)
            Dim psi As New ProcessStartInfo()
            psi.FileName = "cmd.exe"
            psi.Arguments = "/c " + q + batPath + q + " " + q + dwgPath + q + " " + q + acPath + q + " " + q + gsPath + q
            psi.UseShellExecute = False
            psi.CreateNoWindow = True
            psi.WorkingDirectory = dwgDir

            Log("Run: cmd.exe " + psi.Arguments)
            Dim p As Process = Process.Start(psi)
            If Not p.WaitForExit(1500000) Then          ' 25 мин (внешний лимит хоста 30 мин)
                Try
                    p.Kill()
                Catch
                End Try
                Log("Timeout: dwg2pdf.bat did not finish for " + dwgPath)
                Return False
            End If

            Dim code As Integer = p.ExitCode
            If code <> 0 Then
                Log("dwg2pdf.bat exit code " + code + " for " + dwgPath)
                Return False
            End If
            If Not File.Exists(produced) Then
                Log("Mono PDF not produced: " + produced)
                Return False
            End If

            Dim tDir As String = Path.GetDirectoryName(target)
            If Not Directory.Exists(tDir) Then Directory.CreateDirectory(tDir)
            If File.Exists(target) Then File.Delete(target)
            File.Move(produced, target)

            If secTarget <> "" Then
                Try
                    Dim sDir As String = Path.GetDirectoryName(secTarget)
                    If Not Directory.Exists(sDir) Then Directory.CreateDirectory(sDir)
                    If File.Exists(secTarget) Then File.Delete(secTarget)
                    File.Copy(target, secTarget, True)
                Catch exSec As Exception
                    Log("Sec output skipped: " + exSec.Message)
                End Try
            End If

            Log("OK: " + target)
            Return True

        Catch ex As Exception
            Log("Main failed: " + ex.ToString())
            Return False
        End Try

    End Function

End Class

End Namespace
