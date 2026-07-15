using System;
using System.Diagnostics;
using System.IO;

namespace FullVid.Common
{
    // Helpers for the tool child-process environment. yt-dlp discovers an external JS runtime
    // (deno/node) on PATH to solve YouTube's nsig/PO-token challenges; without it, signature
    // deciphering fails and search/download come back empty. Prepending deno's folder to the
    // child PATH lets yt-dlp find it exactly as it would a system install — version-agnostic,
    // no yt-dlp flag that could differ across releases.
    public static class ToolEnv
    {
        // Prepend the directory holding toolExePath to the process's PATH. No-op if the path is
        // blank or the file doesn't exist. Must be called before Process.Start.
        public static void PrependToolDirToPath(ProcessStartInfo psi, string toolExePath)
        {
            if (psi == null || string.IsNullOrWhiteSpace(toolExePath) || !File.Exists(toolExePath))
                return;

            var dir = Path.GetDirectoryName(toolExePath);
            if (string.IsNullOrEmpty(dir))
                return;

            // UseShellExecute=false is required for EnvironmentVariables to take effect.
            var current = psi.EnvironmentVariables.ContainsKey("PATH")
                ? psi.EnvironmentVariables["PATH"]
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            psi.EnvironmentVariables["PATH"] = dir + Path.PathSeparator + current;
        }
    }
}
