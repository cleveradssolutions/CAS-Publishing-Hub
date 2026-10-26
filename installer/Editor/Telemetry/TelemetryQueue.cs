using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CAS.Publishing.Installer.Telemetry
{
    /// <summary>
    /// Append-only JSONL file of pending events. On disk rather than in memory because the wizard
    /// installs across domain reloads (and headless runs exit right after their work), and an
    /// in-memory buffer would lose exactly the events we care about. Main-thread only.
    /// </summary>
    internal sealed class TelemetryQueue
    {
        /// <summary>Oldest lines are dropped past this, so an offline machine can't grow the file forever.</summary>
        public const int MaxLines = 2000;

        /// <summary>Size that triggers a trim. Checked by file length only, so appends stay cheap;
        /// a trim keeps half of <see cref="MaxLines"/>, so it doesn't run again on the next append.</summary>
        public const long MaxBytes = 512 * 1024;

        private readonly string _path;

        public TelemetryQueue(string path) { _path = path; }

        public void Append(string jsonLine)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.AppendAllText(_path, jsonLine.Replace("\n", " ") + "\n");
                TrimIfNeeded();
            }
            catch (Exception) { /* telemetry must never break the wizard */ }
        }

        /// <summary>The first <paramref name="max"/> pending lines, oldest first.</summary>
        public List<string> Peek(int max)
        {
            try
            {
                if (!File.Exists(_path)) return new List<string>();
                return File.ReadLines(_path).Where(l => l.Length > 0).Take(max).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>Deletes everything queued.</summary>
        public void Clear()
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception) { }
        }

        /// <summary>Removes the first <paramref name="count"/> lines (the ones a successful send carried).</summary>
        public void Drop(int count)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var rest = File.ReadLines(_path).Where(l => l.Length > 0).Skip(count).ToList();
                if (rest.Count == 0) File.Delete(_path);
                else File.WriteAllLines(_path, rest);
            }
            catch (Exception) { }
        }

        private void TrimIfNeeded()
        {
            var info = new FileInfo(_path);
            if (info.Length < MaxBytes) return;
            // Keep the newest lines that fit in half the cap (and half the line cap), so the next
            // trim is far away.
            var lines = File.ReadAllLines(_path);
            long bytes = 0;
            var keep = 0;
            for (var i = lines.Length - 1; i >= 0 && keep < MaxLines / 2; i--)
            {
                bytes += lines[i].Length + 1;
                if (bytes > MaxBytes / 2) break;
                keep++;
            }
            File.WriteAllLines(_path, lines.Skip(lines.Length - keep));
        }
    }
}
