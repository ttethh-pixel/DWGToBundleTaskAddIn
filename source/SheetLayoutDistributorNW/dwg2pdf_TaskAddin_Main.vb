' =====================================================================
'  DWG/DXF -> ч/б PDF через dwg2pdf.bat (accoreconsole + Ghostscript)
'  Вставляется в поле сценария задачи SWPDFTaskAddIn вместо DraftSight-тела.
'  Тип задачи ОБЯЗАТЕНО = DWGDXFToPDF (только в этой ветке аддин делает
'  возврат файла в PDM через AddFiles).
'
'  Токены заменяются аддином ПЕРЕД компиляцией (ParseScript):
'    <SourceFolderPath>   - папка локальной копии DWG
'    <SourceFileName>     - имя DWG с расширением
'    [OutputPath]         - ВРЕМЕННЫЙ путь .pdf, откуда аддин заберёт результат
'    [Sec_OutputPath]     - вторичный временный путь (если настроен)
'    [TempErrorLogPath]   - папка логов
'    <TaskInstanceGuid>   - имя файла лога
'
'  Шаблон пути вывода (Office2PDF_OutputPath) задать как:
'    <SourceFolderPath>\<FileName>новый
'  (.pdf дописывает аддин; итог в PDM: <Имя>новый.pdf)
' =====================================================================

Imports System
Imports System.IO
Imports System.Diagnostics
Imports Microsoft.VisualBasic

Namespace SolidWorks

Public Class TaskAddin

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

    Dim batPath As String = "D:\progs\SheetLayoutDistributorNW\dwg2pdf.bat"
    Dim dwgPath As String = Path.Combine("<SourceFolderPath>", "<SourceFileName>")
    Dim target As String = "[OutputPath]"          ' временный .pdf для возврата в PDM
    Dim secTarget As String = "[Sec_OutputPath]"   ' может быть пустой строкой

    Try
        If Not File.Exists(batPath) Then
            Log("dwg2pdf.bat not found: " + batPath)
            Return False
        End If
        If Not File.Exists(dwgPath) Then
            Log("DWG not found: " + dwgPath)
            Return False
        End If

        Dim dwgDir As String = Path.GetDirectoryName(dwgPath)
        Dim base As String = Path.GetFileNameWithoutExtension(dwgPath)
        ' батник кладёт ч/б-результат рядом с DWG как <Имя>новый.pdf
        Dim produced As String = Path.Combine(dwgDir, base + "новый.pdf")

        ' убираем прошлый артефакт, чтобы не принять старый PDF за текущий
        If File.Exists(produced) Then File.Delete(produced)

        Dim q As String = Chr(34)
        Dim psi As New ProcessStartInfo()
        psi.FileName = "cmd.exe"
        psi.Arguments = "/c " + q + batPath + q + " " + q + dwgPath + q
        psi.UseShellExecute = False
        psi.CreateNoWindow = True
        psi.WorkingDirectory = dwgDir

        Log("Run: cmd.exe " + psi.Arguments)
        Dim p As Process = Process.Start(psi)
        If Not p.WaitForExit(1800000) Then          ' 30 минут
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

        ' кладём результат туда, откуда аддин заберёт и добавит в PDM
        Dim tDir As String = Path.GetDirectoryName(target)
        If Not Directory.Exists(tDir) Then Directory.CreateDirectory(tDir)
        If File.Exists(target) Then File.Delete(target)
        File.Move(produced, target)                 ' переносим, чтобы не оставить «мусор» в рабочей папке

        ' вторичный выход, если настроен
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
