using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>Outcome of <see cref="ManifestIO.Read"/>.</summary>
    public enum ManifestReadStatus
    {
        /// <summary>Parsed successfully; <see cref="ManifestReadResult.Root"/> is non-null.</summary>
        Ok,
        /// <summary>File does not exist.</summary>
        FileMissing,
        /// <summary>File exists but could not be read or parsed; <see cref="ManifestReadResult.Error"/> explains.</summary>
        ParseError,
    }

    /// <summary>Result of reading a manifest.json. Distinguishes "absent" and "broken"
    /// from "empty" so callers never treat a malformed manifest as "nothing installed".</summary>
    public readonly struct ManifestReadResult
    {
        public ManifestReadStatus Status { get; }
        /// <summary>Parsed root object; non-null only when <see cref="Status"/> is <see cref="ManifestReadStatus.Ok"/>.</summary>
        public JObject Root { get; }
        public string Error { get; }

        private ManifestReadResult(ManifestReadStatus status, JObject root, string error)
        {
            Status = status; Root = root; Error = error;
        }

        public static ManifestReadResult Ok(JObject root) => new ManifestReadResult(ManifestReadStatus.Ok, root, null);
        public static ManifestReadResult Missing() => new ManifestReadResult(ManifestReadStatus.FileMissing, null, null);
        public static ManifestReadResult Failed(string error) => new ManifestReadResult(ManifestReadStatus.ParseError, null, error);
    }

    /// <summary>
    /// Single robust read/write path for Packages/manifest.json.
    /// Reading tolerates JavaScript-style comments; malformed JSON yields a typed
    /// <see cref="ManifestReadStatus.ParseError"/> rather than a silent empty manifest.
    /// </summary>
    public static class ManifestIO
    {
        private static readonly JsonLoadSettings LoadSettings = new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore,
        };

        /// <summary>Reads and parses the manifest at <paramref name="path"/>.</summary>
        public static ManifestReadResult Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return ManifestReadResult.Missing();

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                return ManifestReadResult.Failed($"read failed: {e.Message}");
            }

            try
            {
                var root = JObject.Parse(text, LoadSettings);
                return ManifestReadResult.Ok(root);
            }
            catch (JsonException e)
            {
                return ManifestReadResult.Failed($"parse failed: {e.Message}");
            }
        }

        /// <summary>
        /// Writes <paramref name="root"/> to <paramref name="path"/> atomically:
        /// serialises to a sibling <c>.tmp</c>, then replaces the target via
        /// <see cref="File.Replace(string,string,string)"/> keeping the prior file as
        /// <c>&lt;path&gt;.bak</c>. 2-space indent; key order follows the JObject.
        /// Throws on I/O failure — callers wrap in try/catch.
        /// </summary>
        public static void WriteAtomic(string path, JObject root)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (root == null) throw new ArgumentNullException(nameof(root));

            var json = root.ToString(Formatting.Indented); // Formatting.Indented = 2-space indent
            var tmp = path + ".tmp";
            var bak = path + ".bak";

            File.WriteAllText(tmp, json); // UTF-8, no BOM

            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, bak); // atomic swap, prior contents → .bak
                else
                    File.Move(tmp, path);
            }
            catch
            {
                // Swap failed (locked file, permissions, …). The original manifest.json
                // is untouched; remove the orphaned temp so it can't be mistaken for one.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }
        }
    }
}
