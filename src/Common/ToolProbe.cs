using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace FullVid.Common
{
    // Validates an external CLI tool (yt-dlp, ffmpeg) by running it with a version flag
    // and reading its version off the first stdout line. Returns a display status string:
    //   "✓ Found · v<version>"  — ran, version parsed
    //   "✓ Found"               — ran (exit 0) but version unparseable
    //   "✗ Not found"           — path empty/missing
    //   "✗ Invalid"             — file exists but wouldn't run / non-zero exit
    // Ported from UniPlaySong's QueryYtDlpVersion/ResolveYtDlpVersionStatus. Caches the
    // parsed version per path+mtime so repeated Settings opens don't re-shell.
    public class ToolProbe
    {
        private readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private struct CacheEntry
        {
            public long MtimeTicks;
            public string Version;
        }

        public string Probe(string toolPath, string versionFlag)
        {
            if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
                return "✗ Not found";

            long mtimeTicks = 0;
            try { mtimeTicks = File.GetLastWriteTimeUtc(toolPath).Ticks; } catch { }

            if (mtimeTicks != 0
                && _cache.TryGetValue(toolPath, out var cached)
                && cached.MtimeTicks == mtimeTicks)
            {
                return FormatStatus(cached.Version);
            }

            // Run returns null when the tool wouldn't run / exited non-zero; empty string
            // when it ran but the version was unparseable; else the parsed version.
            var version = Run(toolPath, versionFlag);
            if (version == null)
                return "✗ Invalid";

            if (mtimeTicks != 0)
                _cache[toolPath] = new CacheEntry { MtimeTicks = mtimeTicks, Version = version };

            return FormatStatus(version);
        }

        private static string FormatStatus(string version)
        {
            return string.IsNullOrEmpty(version) ? "✓ Found" : $"✓ Found · v{version}";
        }

        // Spawns the tool. Returns null when the process wouldn't start, timed out, or exited
        // non-zero. Returns "" when it ran (exit 0) but output was unparseable. Else the version.
        private static string Run(string toolPath, string versionFlag)
        {
            try
            {
                var psi = new ProcessStartInfo(toolPath, versionFlag)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return null;
                    // Drain BOTH streams on background tasks — a full stderr pipe would otherwise
                    // deadlock WaitForExit, and some tools print their version to stderr.
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(3000)) { try { proc.Kill(); } catch { } return null; }
                    if (proc.ExitCode != 0) return null;

                    var stdout = SafeResult(outTask);
                    // Prefer stdout; fall back to stderr (harmless if empty).
                    return ParseVersion(stdout) ?? ParseVersion(SafeResult(errTask)) ?? string.Empty;
                }
            }
            catch
            {
                return null;
            }
        }

        // Awaits a stream-read task with a short bound; returns "" on any fault/timeout.
        private static string SafeResult(System.Threading.Tasks.Task<string> t)
        {
            try { return t.Wait(1000) ? (t.Result ?? string.Empty) : string.Empty; }
            catch { return string.Empty; }
        }

        // yt-dlp prints just the version ("2025.11.12"). ffmpeg prints a multi-line block whose
        // first line is "ffmpeg version 8.0-full_build-... Copyright ...". Take the first
        // non-empty line; for the ffmpeg shape pull the token after "version".
        private static string ParseVersion(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            string firstLine = null;
            foreach (var line in stdout.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) { firstLine = t; break; }
            }
            if (string.IsNullOrEmpty(firstLine)) return null;

            // ffmpeg / ffprobe: "ffmpeg version <VER> Copyright ..." → take word after "version".
            var parts = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                (parts[0].Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
                 parts[0].Equals("ffprobe", StringComparison.OrdinalIgnoreCase)) &&
                parts[1].Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                return parts[2];
            }

            return firstLine;
        }
    }
}
