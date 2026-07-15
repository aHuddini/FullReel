using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FullVid.Common;
using FullVid.Models;
using Newtonsoft.Json.Linq;

namespace FullVid.Services
{
    // Structured YouTube search via yt-dlp. The verified command is:
    //   yt-dlp "ytsearch{N}:{query}" --dump-json --flat-playlist --no-warnings
    // which prints ONE JSON object per line on stdout. ParseSearchJson is the pure,
    // unit-tested seam; SearchAsync is the thin process shell-out around it.
    public class YouTubeSearchService
    {
        private readonly string _ytDlpPath;
        private readonly string _denoPath;

        public YouTubeSearchService(string ytDlpPath, string denoPath = null)
        {
            _ytDlpPath = ytDlpPath;
            _denoPath = denoPath;
        }

        // Parse yt-dlp --dump-json output: one JSON object per line. Blank/whitespace lines
        // are skipped; a malformed line is skipped (never throws). Every field is mapped
        // null-safely — missing duration → TimeSpan.Zero, missing view_count → 0, channel
        // falls back to uploader, thumbnail is thumbnails[0].url or "".
        public static List<VideoResult> ParseSearchJson(IEnumerable<string> jsonLines)
        {
            var results = new List<VideoResult>();
            if (jsonLines == null)
                return results;

            foreach (var line in jsonLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JObject o;
                try
                {
                    o = JObject.Parse(line);
                }
                catch
                {
                    // Not valid JSON (yt-dlp warning leaked to stdout, truncated line, etc.) — skip.
                    continue;
                }

                results.Add(new VideoResult
                {
                    Id = (string)o["id"] ?? string.Empty,
                    Title = (string)o["title"] ?? string.Empty,
                    Duration = o["duration"] != null && o["duration"].Type != JTokenType.Null
                        ? TimeSpan.FromSeconds((double)o["duration"])
                        : TimeSpan.Zero,
                    Channel = (string)o["channel"] ?? (string)o["uploader"] ?? string.Empty,
                    ViewCount = o["view_count"] != null && o["view_count"].Type != JTokenType.Null
                        ? (long)o["view_count"]
                        : 0L,
                    ThumbnailUrl = (string)(o["thumbnails"] as JArray)?.First?["url"] ?? string.Empty,
                    WebpageUrl = (string)o["webpage_url"] ?? string.Empty
                });
            }

            return results;
        }

        // Shell out to yt-dlp and parse the results. query is built by the caller
        // (game name + settings template). Returns empty on cancellation or non-zero exit.
        // Throws InvalidOperationException if yt-dlp path is unset/missing.
        public async Task<List<VideoResult>> SearchAsync(string query, int count, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_ytDlpPath) || !File.Exists(_ytDlpPath))
                throw new InvalidOperationException("yt-dlp path is not configured or does not exist: " + _ytDlpPath);

            if (count < 1)
                count = 1;

            var safeQuery = (query ?? string.Empty).Replace("\"", string.Empty);
            var arguments = $"\"ytsearch{count}:{safeQuery}\" --dump-json --flat-playlist --no-warnings";

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Let yt-dlp find deno (its JS runtime for YouTube signature challenges) on PATH.
            ToolEnv.PrependToolDirToPath(psi, _denoPath);

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return new List<VideoResult>();

                // Drain both streams on background tasks so a full stderr pipe can't
                // deadlock the process while we wait on stdout (UPS pattern).
                var readOutput = Task.Run(() => process.StandardOutput.ReadToEnd());
                var readError = Task.Run(() => process.StandardError.ReadToEnd());

                while (!process.WaitForExit(100))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        try { readOutput.Wait(1000); readError.Wait(1000); } catch { }
                        return new List<VideoResult>();
                    }
                }

                var output = await readOutput.ConfigureAwait(false);
                await readError.ConfigureAwait(false);

                if (process.ExitCode != 0)
                    return new List<VideoResult>();

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return ParseSearchJson(lines);
            }
        }
    }
}
