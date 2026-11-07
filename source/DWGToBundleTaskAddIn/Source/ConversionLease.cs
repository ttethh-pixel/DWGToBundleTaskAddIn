using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DWGToBundleTaskAddIn
{
    internal sealed class ConversionBusyException : IOException
    {
        internal ConversionBusyException(string message) : base(message) { }
    }

    // Global Windows mutexes cover separate processes and interactive/service sessions.
    // The in-process set also rejects nested calls on the same thread (Mutex is recursive).
    internal sealed class ConversionLease : IDisposable
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<string> Active = new HashSet<string>();
        private readonly List<Mutex> _mutexes = new List<Mutex>();
        private readonly List<string> _names = new List<string>();

        internal static ConversionLease Acquire(params string[] resources)
        {
            var lease = new ConversionLease();
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string resource in resources)
            {
                string normalized = Path.GetFullPath(resource).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
                using (var sha = SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "");
                    names.Add((Environment.OSVersion.Platform == PlatformID.Win32NT ? @"Global\" : "") + "DWG2PDF_" + hash);
                }
            }
            try
            {
                foreach (string name in names)
                {
                    lock (Sync)
                    {
                        if (!Active.Add(name)) throw new ConversionBusyException("Этот исходник или путь PDF уже обрабатывается. Повторите задачу после завершения текущей.");
                        lease._names.Add(name);
                    }
                    var mutex = new Mutex(false, name);
                    bool taken = false;
                    try
                    {
                        try { taken = mutex.WaitOne(0); }
                        catch (AbandonedMutexException)
                        {
                            taken = true;
                            throw new ConversionBusyException("Предыдущий процесс оборвался. Проверьте процессы AutoCAD и pdf-recovery перед повторным запуском.");
                        }
                        if (!taken) throw new ConversionBusyException("Этот исходник или путь PDF занят другим процессом. Повторите задачу позже.");
                        lease._mutexes.Add(mutex);
                    }
                    catch
                    {
                        if (taken) mutex.ReleaseMutex();
                        mutex.Dispose();
                        throw;
                    }
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (int i = _mutexes.Count - 1; i >= 0; i--)
            {
                _mutexes[i].ReleaseMutex();
                _mutexes[i].Dispose();
            }
            _mutexes.Clear();
            lock (Sync) { foreach (string name in _names) Active.Remove(name); }
            _names.Clear();
        }
    }
}
