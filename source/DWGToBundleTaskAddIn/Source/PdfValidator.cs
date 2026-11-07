using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DWGToBundleTaskAddIn
{
    // Checks rendered page coverage, not compressed file size or PDF text objects.
    internal static class PdfValidator
    {
        internal const string DefaultGhostscript = @"C:\Program Files\PDFCreator\Ghostscript\bin\gswin32c.exe";
        private static readonly Regex CoverageLine = new Regex(
            @"^\s*([0-9]+\.[0-9]+)\s+([0-9]+\.[0-9]+)\s+([0-9]+\.[0-9]+)\s+([0-9]+\.[0-9]+)\s+CMYK\s+OK\s*$",
            RegexOptions.CultureInvariant);

        internal static string ResolveGhostscript(string configured)
        {
            string path = configured;
            if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable("DWG2PDF_GS");
            if (string.IsNullOrWhiteSpace(path)) path = DefaultGhostscript;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(path))
                throw new FileNotFoundException("Не найден Ghostscript для проверки PDF. Укажите путь в настройках задачи или DWG2PDF_GS.", path);
            return Path.GetFullPath(path);
        }

        internal static int Validate(string pdf, string ghostscript, string tempDirectory, int timeoutMilliseconds)
        {
            if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
                throw new InvalidDataException("PDF отсутствует или имеет нулевой размер.");
            if (timeoutMilliseconds <= 0) throw new TimeoutException("Истёк таймаут до проверки PDF.");
            string report = Path.Combine(tempDirectory, "ink-" + Guid.NewGuid().ToString("N") + ".txt");
            var diagnostics = new StringBuilder();
            object sync = new object();
            var psi = new ProcessStartInfo(ghostscript,
                "-dSAFER -dBATCH -dNOPAUSE -dPDFSTOPONERROR -dPDFSTOPONWARNING -dUseCropBox -r150 -sDEVICE=inkcov " +
                "-sOutputFile=" + Quote(report) + " -f " + Quote(Path.GetFullPath(pdf)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            try
            {
                int exit;
                using (var process = new Process())
                {
                    process.StartInfo = psi;
                    DataReceivedEventHandler capture = (sender, e) =>
                    {
                        if (e.Data == null) return;
                        lock (sync) { if (diagnostics.Length < 32000) diagnostics.AppendLine(e.Data); }
                    };
                    process.OutputDataReceived += capture;
                    process.ErrorDataReceived += capture;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try { process.Kill(); process.WaitForExit(5000); } catch { }
                        throw new TimeoutException("Истёк таймаут проверки PDF через Ghostscript.");
                    }
                    process.WaitForExit(); // drain redirected streams before evaluating the result
                    exit = process.ExitCode;
                }
                string log; lock (sync) log = diagnostics.ToString();
                if (exit != 0)
                    throw new InvalidDataException("Ghostscript не смог проверить PDF (код " + exit + "): " + log.Trim());
                if (!File.Exists(report)) throw new InvalidDataException("Ghostscript не выдал отчёт проверки PDF.");
                return ParseCoverage(File.ReadAllText(report));
            }
            finally { try { File.Delete(report); } catch { } }
        }

        // Fail closed: no pages, an unreadable report, or any white page rejects the whole PDF.
        internal static int ParseCoverage(string report)
        {
            int pages = 0;
            var blankPages = new List<string>();
            using (var reader = new StringReader(report ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Match match = CoverageLine.Match(line);
                    if (!match.Success) throw new InvalidDataException("Некорректный отчёт покрытия PDF: " + line);
                    pages++;
                    double total = 0;
                    for (int i = 1; i <= 4; i++)
                    {
                        double value = double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture);
                        if (double.IsInfinity(value) || value < 0 || value > 1)
                            throw new InvalidDataException("Некорректное значение покрытия PDF.");
                        total += value;
                    }
                    if (total == 0) blankPages.Add(pages.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (pages == 0) throw new InvalidDataException("PDF не содержит страниц, доступных для печати.");
            if (blankPages.Count > 0)
                throw new InvalidDataException("Белые страницы PDF: " + string.Join(", ", blankPages.ToArray()) + ".");
            return pages;
        }

        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0) throw new ArgumentException("Недопустимые кавычки в пути.");
            return "\"" + value + "\"";
        }
    }
}
