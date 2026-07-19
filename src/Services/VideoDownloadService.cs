using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FullVid.Common;
using FullVid.Models;
using Playnite.SDK.Models;

namespace FullVid.Services
{
    // Downloads a chosen YouTube video and transcodes it into ExtraMetadataLoader's exact
    // format, landing it at <config>\ExtraMetadata\games\<guid>\VideoTrailer.mp4 so EML
    // plays it as the game's trailer. Two shell-outs (yt-dlp then ffmpeg) following UPS's
    // YouTubeDownloader process pattern: args built as one string, both stdout/stderr drained
    // on background tasks to avoid a full-pipe deadlock, Kill() on cancel, exit-code + output
    // -file checks. Pure I/O-free work is minimal; the class is a thin process orchestrator.
    public class VideoDownloadService
    {
        private readonly FullVidSettings _settings;
        private readonly string _configurationPath;
        private readonly string _tempRoot;
        private readonly FileLogger _fileLogger;
        private readonly ToolProbe _probe = new ToolProbe();

        // configurationPath MUST be PlayniteApi.Paths.ConfigurationPath (portable-correct).
        // tempRoot is where the intermediate download lands; null falls back to the OS temp dir.
        public VideoDownloadService(FullVidSettings settings, string configurationPath, string tempRoot = null, FileLogger fileLogger = null)
        {
            _settings = settings;
            _configurationPath = configurationPath;
            _tempRoot = string.IsNullOrWhiteSpace(tempRoot) ? Path.GetTempPath() : tempRoot;
            _fileLogger = fileLogger;
        }

        public async Task<bool> DownloadAsync(VideoResult video, Game game, IProgress<string> progress, CancellationToken ct)
        {
            if (video == null || game == null)
            {
                progress?.Report("Nothing to download.");
                return false;
            }

            var ytDlpPath = _settings?.YtDlpPath;
            var ffmpegPath = _settings?.FfmpegPath;

            // Gate: both tools must validate before we start. ToolProbe returns a "✓ Found"
            // status only when the binary actually ran; anything else means it's missing/invalid.
            if (!_probe.Probe(ytDlpPath, "--version").StartsWith("✓"))
            {
                progress?.Report("yt-dlp is not configured or invalid. Set its path in FullReel settings.");
                return false;
            }
            if (!_probe.Probe(ffmpegPath, "-version").StartsWith("✓"))
            {
                progress?.Report("FFmpeg is not configured or invalid. Set its path in FullReel settings.");
                return false;
            }

            // Prefer the full webpage URL; fall back to the video id (search hits can lack the URL).
            var url = !string.IsNullOrWhiteSpace(video.WebpageUrl)
                ? video.WebpageUrl
                : "https://www.youtube.com/watch?v=" + (video.Id ?? string.Empty);
            if (string.IsNullOrWhiteSpace(video.Id) && string.IsNullOrWhiteSpace(video.WebpageUrl))
            {
                progress?.Report("This result has no video URL to download.");
                return false;
            }

            var emlPath = EmlPaths.GetVideoTrailerPath(_configurationPath, game.Id);
            var tmpMp4 = emlPath + ".tmp.mp4";

            // yt-dlp writes {tempBase}.{ext}; the actual ext is unknown until it runs, so we
            // glob the basename afterwards to resolve the real produced file.
            var tempBase = Path.Combine(_tempRoot, "fullreel-" + Guid.NewGuid().ToString("N"));
            string downloaded = null;

            try
            {
                Directory.CreateDirectory(_tempRoot);

                // Step 1: yt-dlp download.
                progress?.Report("Downloading video...");
                var ytArgs = BuildYtDlpArgs(url, tempBase);
                Log("yt-dlp " + ytArgs);
                var ytOk = await RunProcessAsync(ytDlpPath, ytArgs, progress, ct, isYtDlp: true).ConfigureAwait(false);
                if (!ytOk)
                {
                    progress?.Report(ct.IsCancellationRequested ? "Download cancelled." : "Download failed.");
                    return false;
                }

                downloaded = ResolveDownloadedFile(tempBase);
                if (downloaded == null)
                {
                    progress?.Report("Download failed (no output file produced).");
                    return false;
                }

                // Create the EML game folder up front — ffmpeg writes tmpMp4 INTO it (tmpMp4 =
                // emlPath + ".tmp.mp4"), so the folder must exist before the transcode, not after.
                // A game with no prior ExtraMetadata folder gets one created here.
                Directory.CreateDirectory(Path.GetDirectoryName(emlPath));

                // Step 2: get the download into EML format. When yt-dlp landed an .mp4 (the
                // format selector prefers avc1+mp4a, so this is the common case), a container
                // remux (-c copy) finishes in seconds. Anything else — or a failed remux —
                // takes the full libx264 re-encode path.
                var ffOk = false;
                if (downloaded.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report("Packaging trailer (no re-encode)...");
                    var remuxArgs = $"-y -i \"{downloaded}\" -c copy -movflags +faststart \"{tmpMp4}\"";
                    Log("ffmpeg (remux) " + remuxArgs);
                    ffOk = await RunProcessAsync(ffmpegPath, remuxArgs, progress, ct, isYtDlp: false,
                        tickerLabel: "Packaging trailer...").ConfigureAwait(false);
                }
                if (!ffOk && !ct.IsCancellationRequested)
                {
                    var ffArgs = BuildFfmpegArgs(downloaded, tmpMp4);
                    Log("ffmpeg " + ffArgs);
                    ffOk = await RunProcessAsync(ffmpegPath, ffArgs, progress, ct, isYtDlp: false,
                        tickerLabel: "Converting to trailer format...").ConfigureAwait(false);
                }
                if (!ffOk || !File.Exists(tmpMp4))
                {
                    progress?.Report(ct.IsCancellationRequested ? "Download cancelled." : "Conversion failed.");
                    return false;
                }

                // Step 3: replace the existing trailer. ExtraMetadataLoader keeps the current
                // game's VideoTrailer.mp4 open (its player has a handle), so a plain
                // Delete/Move throws a sharing violation when replacing a just-played trailer.
                // Retry with backoff (EML releases the handle shortly), then report clearly.
                if (!ReplaceTrailer(tmpMp4, emlPath))
                {
                    progress?.Report("Couldn't replace the existing trailer — it may be open in " +
                        "ExtraMetadataLoader. Select a different game (or restart Playnite), then try again.");
                    return false;
                }

                progress?.Report("Done.");
                Log("VideoTrailer written: " + emlPath);
                return true;
            }
            catch (OperationCanceledException)
            {
                progress?.Report("Download cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Log("Download error: " + ex);
                progress?.Report("Download failed: " + ex.Message);
                return false;
            }
            finally
            {
                // Step 4: clean temp files — the resolved download, any stray same-basename
                // output, and a leftover .tmp.mp4 from a failed/cancelled transcode.
                CleanupTemp(tempBase, downloaded, tmpMp4);
            }
        }

        // -f selector from DownloadQuality; falls back to the brief's "bv*+ba/b" for Best.
        // --no-warnings keeps stray warnings off stdout so progress parsing stays clean.
        // Cookies wired from CookiesSource / CustomCookiesFilePath (see BuildCookieArgs).
        private string BuildYtDlpArgs(string url, string tempBase)
        {
            var format = FormatSelector(_settings?.DownloadQuality ?? VideoQuality.Best);
            var cookies = BuildCookieArgs();
            return $"-f \"{format}\" -o \"{tempBase}.%(ext)s\"{cookies} --no-warnings \"{url}\"";
        }

        // Prefer H.264 (avc1) video + AAC (mp4a) audio within each tier: EML's target format is
        // H.264/AAC, so an avc1+mp4a download lands as .mp4 and only needs a container remux
        // (seconds) instead of a full libx264 re-encode (minutes for a 1080p60/4K VP9 pick).
        // Move src onto dest, replacing an existing dest even when it's transiently locked by
        // ExtraMetadataLoader's player. Retries the delete+move a few times with backoff; returns
        // false only if it stays locked. File.Move won't overwrite on .NET Fx, hence delete-first.
        private bool ReplaceTrailer(string src, string dest)
        {
            const int attempts = 6;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(src, dest);
                    return true;
                }
                catch (IOException) { }            // sharing violation — EML still holds the file
                catch (UnauthorizedAccessException) { }
                if (i < attempts - 1)
                    System.Threading.Thread.Sleep(400);
            }
            Log("ReplaceTrailer: destination stayed locked after retries: " + dest);
            return false;
        }

        private static string FormatSelector(VideoQuality q)
        {
            switch (q)
            {
                case VideoQuality.P1080: return "bv*[vcodec^=avc1][height<=1080]+ba[acodec^=mp4a]/bv*[height<=1080]+ba/b[height<=1080]";
                case VideoQuality.P720: return "bv*[vcodec^=avc1][height<=720]+ba[acodec^=mp4a]/bv*[height<=720]+ba/b[height<=720]";
                case VideoQuality.P480: return "bv*[vcodec^=avc1][height<=480]+ba[acodec^=mp4a]/bv*[height<=480]+ba/b[height<=480]";
                default: return "bv*[vcodec^=avc1]+ba[acodec^=mp4a]/bv*+ba/b";
            }
        }

        // -preset veryfast: 3-5x faster than the default (medium) for marginal size cost — a
        // trailer re-encode shouldn't take minutes. Only the fallback path pays this at all.
        private string BuildFfmpegArgs(string input, string output)
        {
            return $"-y -i \"{input}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -movflags +faststart \"{output}\"";
        }

        // Wires the persisted CookiesSource + CustomCookiesFilePath into yt-dlp args:
        //   browser value  → --cookies-from-browser <browser>
        //   CustomFile + a real file → --cookies "<path>"
        //   None (or CustomFile with no valid file) → no cookie args
        private string BuildCookieArgs()
        {
            switch (_settings?.CookiesSource ?? CookieMode.None)
            {
                case CookieMode.Firefox: return " --cookies-from-browser firefox";
                case CookieMode.Chrome: return " --cookies-from-browser chrome";
                case CookieMode.Edge: return " --cookies-from-browser edge";
                case CookieMode.Brave: return " --cookies-from-browser brave";
                case CookieMode.Opera: return " --cookies-from-browser opera";
                case CookieMode.CustomFile:
                    var file = _settings?.CustomCookiesFilePath;
                    return !string.IsNullOrWhiteSpace(file) && File.Exists(file) ? $" --cookies \"{file}\"" : string.Empty;
                default: return string.Empty;
            }
        }

        // yt-dlp appends the real extension, so resolve by globbing the basename. Prefer a
        // fully-muxed container if present; else take whatever single file was produced.
        private static string ResolveDownloadedFile(string tempBase)
        {
            var dir = Path.GetDirectoryName(tempBase);
            var stem = Path.GetFileName(tempBase);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var matches = Directory.GetFiles(dir, stem + ".*");
            if (matches.Length == 0)
                return null;

            foreach (var ext in new[] { ".mp4", ".mkv", ".webm" })
            {
                foreach (var m in matches)
                    if (m.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        return m;
            }
            return matches[0];
        }

        // Runs a process, draining stdout+stderr on background tasks (full-pipe deadlock
        // guard). For yt-dlp, parses download-percent lines off stdout into progress.
        // tickerLabel (ffmpeg) reports "{label} {n}s" every ~2s so a long encode never looks
        // hung. Kills the process if the token trips. Returns true only on exit code 0.
        private async Task<bool> RunProcessAsync(string exePath, string args, IProgress<string> progress, CancellationToken ct, bool isYtDlp, string tickerLabel = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // yt-dlp needs deno on PATH to solve YouTube's signature/PO-token challenges;
            // ffmpeg doesn't, so only inject for the yt-dlp process.
            if (isYtDlp)
                ToolEnv.PrependToolDirToPath(psi, _settings?.DenoPath);

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return false;

                var readOutput = Task.Run(() =>
                {
                    if (isYtDlp && progress != null)
                    {
                        // Read line-by-line so we can surface yt-dlp's [download] NN% lines.
                        string line;
                        var sb = new System.Text.StringBuilder();
                        while ((line = process.StandardOutput.ReadLine()) != null)
                        {
                            sb.AppendLine(line);
                            var pct = ParseProgressPercent(line);
                            if (pct != null)
                                progress.Report("Downloading video... " + pct);
                        }
                        return sb.ToString();
                    }
                    return process.StandardOutput.ReadToEnd();
                });
                var readError = Task.Run(() => process.StandardError.ReadToEnd());

                var started = DateTime.Now;
                var lastTick = started;
                while (!process.WaitForExit(100))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        try { readOutput.Wait(1000); readError.Wait(1000); } catch { }
                        return false;
                    }

                    // Elapsed-seconds heartbeat so a multi-minute encode visibly ticks along.
                    if (tickerLabel != null && progress != null && (DateTime.Now - lastTick).TotalSeconds >= 2)
                    {
                        lastTick = DateTime.Now;
                        progress.Report($"{tickerLabel} {(int)(lastTick - started).TotalSeconds}s");
                    }
                }

                await readOutput.ConfigureAwait(false);
                var err = await readError.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    Log(Path.GetFileName(exePath) + " exit " + process.ExitCode + ": " + err);
                    return false;
                }
                return true;
            }
        }

        // yt-dlp prints "[download]  42.3% of ...". Pull the percent token; null if none.
        private static string ParseProgressPercent(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf("[download]", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            var m = Regex.Match(line, @"(\d{1,3}(?:\.\d+)?)%");
            return m.Success ? m.Groups[1].Value + "%" : null;
        }

        private static void CleanupTemp(string tempBase, string downloaded, string tmpMp4)
        {
            TryDelete(downloaded);
            TryDelete(tmpMp4);
            try
            {
                var dir = Path.GetDirectoryName(tempBase);
                var stem = Path.GetFileName(tempBase);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, stem + ".*"))
                        TryDelete(f);
                }
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void Log(string message)
        {
            _fileLogger?.Info("[VideoDownloadService] " + message);
        }
    }
}
