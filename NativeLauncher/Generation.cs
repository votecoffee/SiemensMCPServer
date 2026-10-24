using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SiemensMCPServer.NativeLauncher
{
    internal sealed class Generation : IDisposable
    {
        private readonly string root, path, id;
        private readonly object owner;
        private readonly Selection selection;
        private int? exitCode;
        internal Generation(Selection selection) {
            this.selection = selection; root = selection.Root; id = Guid.NewGuid().ToString("N");
            using var process = Process.GetCurrentProcess();
            owner = new { pid = process.Id, creationFileTime = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture), image = process.MainModule!.FileName };
            string active = Selection.Safe(root, "state/active"); Directory.CreateDirectory(active);
            using var mutex = new Mutex(false, "Local\\SiemensNativeLauncherLedger-" + Selection.Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
            bool acquired; try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Native generation ledger is busy.");
            try {
                if (Directory.GetFiles(active, "*.json").Length >= 128) throw new InvalidDataException("Retained native generation bound reached; review receipts.");
                path = Selection.Safe(root, "state/active/" + id + ".json");
                Write(path, Receipt("active"));
            } finally { mutex.ReleaseMutex(); }
        }
        private object Receipt(string state) => new { schemaVersion = 1, generation = id, owner, snapshotSha256 = selection.SnapshotHash, version = selection.Data.Version, state, exitCode,
            nativeOutcome = "Inspect exact native attachment/journal evidence; a host exit code is not engineering acceptance." };
        private static void Write(string path, object value) {
            Selection.NoLinks(path); byte[] raw = JsonSerializer.SerializeToUtf8Bytes(value, Selection.Json);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(raw, 0, raw.Length); stream.Flush(true);
        }
        internal void Complete(int code) { exitCode = code; }
        public void Dispose() {
            string receipt = Selection.Safe(root, "state/receipts/" + id); Directory.CreateDirectory(receipt);
            // Original claim is retained, including after a failed/uncertain host.
            File.Move(path, Path.Combine(receipt, "claim.json")); Write(Path.Combine(receipt, "outcome.json"), Receipt(exitCode == 0 ? "closed" : "failed-or-uncertain"));
        }
    }
}
