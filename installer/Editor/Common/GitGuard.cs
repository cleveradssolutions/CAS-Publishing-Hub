using System;
using System.Diagnostics;
using System.IO;

namespace CAS.Publishing.Installer.Common
{
    /// <summary>
    /// The migrator has no backup of its own; it relies on the client's git for undo.
    /// This guard verifies that reliance actually holds for a given path before any
    /// irreversible deletion: the path must be inside a git work tree, tracked, and clean.
    /// On ANY uncertainty (not a repo, git missing, untracked, ignored, or dirty) it returns
    /// false with a reason — callers MUST refuse to delete.
    /// </summary>
    public static class GitGuard
    {
        public static bool IsTrackedAndClean(string absolutePath, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(absolutePath)) { reason = "empty path"; return false; }

            var dir = Directory.Exists(absolutePath) ? absolutePath : Path.GetDirectoryName(absolutePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { reason = "path does not exist"; return false; }

            // Tracked? (also fails for git-ignored paths — exactly what we want to refuse.)
            if (!TryRunGit(dir, new[] { "ls-files", "--error-unmatch", absolutePath }, out _, out var lsErr))
            {
                reason = $"path is not tracked by git (untracked or ignored): {Trim(lsErr)}";
                return false;
            }

            // Clean? (staged/unstaged/untracked changes under the path → non-empty output)
            if (!TryRunGit(dir, new[] { "status", "--porcelain", "--", absolutePath }, out var statusOut, out var statusErr))
            {
                reason = $"git status failed: {Trim(statusErr)}";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(statusOut))
            {
                reason = "path has uncommitted changes — commit or stash before migrating";
                return false;
            }

            return true;
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim();

        private static bool TryRunGit(string workingDir, string[] args, out string stdout, out string stderr)
        {
            stdout = null; stderr = null;
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using (var p = Process.Start(psi))
                {
                    stdout = p.StandardOutput.ReadToEnd();
                    stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } stderr = "git timed out"; return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception e) { stderr = e.Message; return false; }
        }
    }
}
