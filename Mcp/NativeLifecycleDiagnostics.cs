using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace SiemensMCPServer.Mcp
{
    // Diagnostics are stage-only. They are neither a terminal attestation nor
    // permission to retire a launcher claim or clear an engineering barrier.
    internal sealed class NativeStageRecorder
    {
        private readonly string path;
        private readonly object identity;
        private readonly int maximumEvents, maximumBytes;
        private readonly Queue<byte[]> pending = new Queue<byte[]>();
        private readonly object sync = new object();
        private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);
        private int count;
        private bool writing, unavailable;
        internal NativeStageRecorder(string path, object identity, int maximumEvents = 64, int maximumBytes = 32768)
        {
            if (maximumEvents < 1 || maximumEvents > 128 || maximumBytes < 1024 || maximumBytes > 131072) throw new ArgumentException("Invalid diagnostic bounds.");
            this.path = Path.GetFullPath(path); this.identity = identity; this.maximumEvents = maximumEvents; this.maximumBytes = maximumBytes;
        }
        internal void Record(string stage, object? details = null)
        {
            if (string.IsNullOrEmpty(stage) || stage.Length > 64) return;
            byte[] raw = JsonSerializer.SerializeToUtf8Bytes(new { format = "siemens-native-stage-v1", utc = DateTime.UtcNow.ToString("o"), identity, stage,
                details, observedTerminal = "UNKNOWN", nativeOutcome = "Inspect native journals separately; this is lifecycle metadata." });
            if (raw.Length > 4096) return;
            lock (sync) {
                if (unavailable || count >= maximumEvents) return;
                count++; pending.Enqueue(raw); idle.Reset();
                if (writing) return;
                writing = true;
            }
            ThreadPool.QueueUserWorkItem(state => {
                try {
                    Ordinary(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); Ordinary(path);
                    using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    while (true) {
                        byte[]? next = null;
                        lock (sync) {
                            if (pending.Count != 0) next = pending.Dequeue();
                        }
                        if (next == null) {
                            file.Flush(true); // never hold the producer lock during file IO
                            lock (sync) { if (pending.Count == 0) { writing = false; idle.Set(); return; } }
                            continue;
                        }
                        if (file.Length + next.Length + 1 > maximumBytes) { file.Flush(true); lock (sync) { unavailable = true; pending.Clear(); writing = false; idle.Set(); } return; }
                        file.Write(next, 0, next.Length); file.WriteByte(10);
                    }
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) {
                    lock (sync) { unavailable = true; pending.Clear(); writing = false; idle.Set(); } // never recursively log to stderr
                }
            });
        }
        private static void Ordinary(string value)
        {
            for (string? p = value; p != null; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked diagnostic path refused.");
        }
        internal bool Flush(int milliseconds) => idle.Wait(Math.Max(0, Math.Min(250, milliseconds)));
        internal bool Unavailable { get { lock (sync) return unavailable; } }
    }
    internal static class NativeLifecycleDiagnostics
    {
        private static NativeStageRecorder? recorder;
        internal static void Start()
        {
            // Direct model fixtures and --version must not create production evidence.
            try {
                using var process = Process.GetCurrentProcess();
                string image = process.MainModule!.FileName;
                if (!string.Equals(Path.GetFileName(image), "SiemensMCPServer.NativeLauncher.exe", StringComparison.OrdinalIgnoreCase)) return;
                string root = Path.GetDirectoryName(image)!;
                var owner = new { pid = process.Id, creationFileTime = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture), image };
                recorder = new NativeStageRecorder(Path.Combine(root, "state", "launcher-stages", process.Id + "-" + owner.creationFileTime + ".jsonl"), owner);
                Record("backend-entered");
            } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is System.Security.SecurityException) { recorder = null; }
        }
        internal static void Record(string stage, object? details = null) => recorder?.Record(stage, details);
        internal static void Flush() => recorder?.Flush(25);
    }
}
