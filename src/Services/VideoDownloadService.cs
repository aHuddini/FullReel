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

                // Step 0: ask yt-dlp what it WOULD pick, and what YouTube actually offered, before
                // committing to a download. Costs one metadata round-trip and is the difference
                // between "my trailer is 720p" and knowing exactly why: the probe reports both the
                // chosen format and the full ladder of available heights, so a ceiling imposed by
                // YouTube (restricted client, stale yt-dlp, no cookies) is distinguishable from a
                // selector bug on our side.
                var plan = await ProbeFormatsAsync(ytDlpPath, url, ct).ConfigureAwait(false);
                var wanted = _settings?.DownloadQuality ?? VideoQuality.Best;

                // A ceiling of 720p or less means YouTube handed this client only the pre-muxed
                // rungs — no adaptive ladder exists to choose from, so the quality setting is
                // powerless. Retry once against a client that does expose the ladder before
                // accepting a 360p trailer. Only fires on the evidence of a low ceiling, and only
                // when the user has not already pinned a client of their own.
                var activeClient = _settings?.YtDlpPlayerClient;
                if (plan != null
                    && plan.MaxAvailable > 0 && plan.MaxAvailable <= 720
                    && _settings?.AutoClientFallback != false
                    && string.IsNullOrWhiteSpace(activeClient))
                {
                    Log($"Ceiling {plan.MaxAvailable}p with yt-dlp's default client — retrying with player_client={FallbackPlayerClient}.");
                    progress?.Report("Only low quality offered — retrying...");

                    var retry = await ProbeFormatsAsync(ytDlpPath, url, FallbackPlayerClient, ct).ConfigureAwait(false);
                    if (retry != null && retry.MaxAvailable > plan.MaxAvailable)
                    {
                        Log($"Fallback client improved the ceiling {plan.MaxAvailable}p -> {retry.MaxAvailable}p; using it for this download.");
                        plan = retry;
                        activeClient = FallbackPlayerClient;
                    }
                    else
                    {
                        // Genuinely nothing better on offer — say so rather than silently retrying forever.
                        Log("Fallback client did not improve the ceiling. yt-dlp is likely out of date, " +
                            "or this video really is only available in low quality.");
                    }
                }

                if (plan != null)
                {
                    Log($"Requested={QualityLabel(wanted)}  Picked={plan.Describe()}  AvailableHeights=[{plan.AvailableCsv}]");

                    var cap = HeightCap(wanted);
                    if (plan.MaxAvailable > 0 && cap > 0 && plan.MaxAvailable < cap)
                    {
                        // YouTube never offered anything this tall — not our selector's doing.
                        progress?.Report($"Note: {cap}p not offered for this video (best available {plan.MaxAvailable}p).");
                        Log($"CEILING: YouTube offered at most {plan.MaxAvailable}p for this video.");
                    }
                    if (plan.MaxAvailable > 0 && plan.MaxAvailable <= 720)
                    {
                        // The classic restricted-format signature: YouTube served only the
                        // pre-muxed rungs, which top out at 720p, so every quality choice
                        // collapses to the same file.
                        Log("CEILING <=720p — YouTube returned only the limited/pre-muxed format set. " +
                            "Usual causes: outdated yt-dlp, or this client needing cookies / a PO-token provider (deno).");
                    }
                }
                else
                {
                    Log("Format probe failed; continuing with the download anyway.");
                }

                // Step 1: yt-dlp download.
                progress?.Report(plan != null
                    ? $"Downloading {plan.Describe()}..."
                    : "Downloading video...");
                var ytArgs = BuildYtDlpArgs(url, tempBase, activeClient);
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
                    var ffArgs = BuildFfmpegArgs(downloaded, tmpMp4, ffmpegPath);
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
        // playerClient carries the probe's decision through to the real download, so the download
        // cannot end up asking a different client than the one that reported the format ladder.
        private string BuildYtDlpArgs(string url, string tempBase, string playerClient)
        {
            var format = FormatSelector(_settings?.DownloadQuality ?? VideoQuality.Best);
            var cookies = BuildCookieArgs();

            // yt-dlp merges bv*+ba with ITS OWN ffmpeg lookup, which searches PATH — it does not
            // know about the ffmpeg we validated in settings. A user who pointed FullReel at an
            // ffmpeg.exe that isn't also on PATH got no merge at all: yt-dlp wrote the video and
            // audio as two separate files and --no-warnings swallowed the explanation, so the
            // trailer was packaged from the video-only stream and played silent.
            var ffmpegDir = GetFfmpegLocationArg();

            return $"-f \"{format}\" -S \"{FormatSort}\" -o \"{tempBase}.%(ext)s\"" +
                   $"{ExtractorArgs(playerClient)}{ffmpegDir}{cookies} --no-warnings \"{url}\"";
        }

        // What the probe learned: the format yt-dlp intends to take, and every height YouTube
        // listed. MaxAvailable is the honest ceiling — if it is below what the user asked for,
        // no format selector could have done better.
        private class FormatPlan
        {
            public int Height;
            public string FormatId;
            public string Vcodec;
            public string Fps;
            public int MaxAvailable;
            public string AvailableCsv = string.Empty;

            public string Describe()
            {
                var res = Height > 0 ? Height + "p" : "unknown";
                if (!string.IsNullOrEmpty(Fps) && Fps != "None" && Fps != "0") res += Fps;
                var codec = string.IsNullOrEmpty(Vcodec) || Vcodec == "None" ? "" : " " + ShortCodec(Vcodec);
                var id = string.IsNullOrEmpty(FormatId) ? "" : $" [{FormatId}]";
                return res + codec + id;
            }

            private static string ShortCodec(string v)
            {
                if (v.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)) return "H.264";
                if (v.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) || v.StartsWith("vp09", StringComparison.OrdinalIgnoreCase)) return "VP9";
                if (v.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return "AV1";
                return v;
            }
        }

        // Single --simulate call that prints two tagged lines: the format that would be chosen,
        // and the list of every height YouTube advertised.
        private async Task<FormatPlan> ProbeFormatsAsync(string ytDlpPath, string url, CancellationToken ct)
        {
            return await ProbeFormatsAsync(ytDlpPath, url, _settings?.YtDlpPlayerClient, ct).ConfigureAwait(false);
        }

        private async Task<FormatPlan> ProbeFormatsAsync(string ytDlpPath, string url, string playerClient, CancellationToken ct)
        {
            try
            {
                var format = FormatSelector(_settings?.DownloadQuality ?? VideoQuality.Best);
                var args = $"-f \"{format}\" -S \"{FormatSort}\"" +
                           " --print \"FVPICK|%(height)s|%(format_id)s|%(vcodec)s|%(fps)s\"" +
                           " --print \"FVAVAIL|%(formats.:.height)s\"" +
                           $" --simulate --no-warnings{ExtractorArgs(playerClient)}{GetFfmpegLocationArg()}{BuildCookieArgs()} \"{url}\"";

                var output = await RunAndCaptureAsync(ytDlpPath, args, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return null;

                var plan = new FormatPlan();
                var gotPick = false;
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("FVPICK|", StringComparison.Ordinal))
                    {
                        var f = line.Split('|');
                        if (f.Length >= 5)
                        {
                            int.TryParse(f[1], out plan.Height);
                            plan.FormatId = f[2];
                            plan.Vcodec = f[3];
                            plan.Fps = f[4];
                            gotPick = true;
                        }
                    }
                    else if (line.StartsWith("FVAVAIL|", StringComparison.Ordinal))
                    {
                        // "[27, 45, 144, ..., 2160]" — take the numbers and keep the max.
                        var nums = Regex.Matches(line, @"\d+");
                        var seen = new System.Collections.Generic.SortedSet<int>();
                        foreach (Match m in nums)
                        {
                            if (int.TryParse(m.Value, out var h) && h > 0) seen.Add(h);
                        }
                        if (seen.Count > 0)
                        {
                            plan.MaxAvailable = 0;
                            foreach (var h in seen) if (h > plan.MaxAvailable) plan.MaxAvailable = h;
                            plan.AvailableCsv = string.Join(",", seen);
                        }
                    }
                }
                return gotPick ? plan : null;
            }
            catch (Exception ex)
            {
                Log("Format probe error: " + ex.Message);
                return null;
            }
        }

        // Minimal runner for the probe: no progress parsing, just stdout.
        private async Task<string> RunAndCaptureAsync(string exePath, string args, CancellationToken ct)
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
            ToolEnv.PrependToolDirToPath(psi, _settings?.DenoPath);

            using (var process = Process.Start(psi))
            {
                if (process == null) return null;
                var so = Task.Run(() => process.StandardOutput.ReadToEnd());
                var se = Task.Run(() => process.StandardError.ReadToEnd());
                while (!process.WaitForExit(100))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                }
                var outText = await so.ConfigureAwait(false);
                var errText = await se.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    Log("probe exit " + process.ExitCode + ": " + errText);
                    return null;
                }
                return outText;
            }
        }

        // Clients yt-dlp can ask YouTube as. Empty string == let yt-dlp choose, which is the
        // default and the preferred path: yt-dlp maintains that list against YouTube's changes,
        // and anything hardcoded here eventually rots when a client is retired.
        //
        // The fallback exists because YouTube's SABR rollout leaves several clients able to return
        // ONLY pre-muxed formats — itag 18 (360p) or 22 (720p) — with no adaptive ladder at all.
        // A user whose yt-dlp lands on one of those gets 360p no matter what quality they picked,
        // which is not something a format selector can fix. Measured against a 4K video: the
        // "android" client exposed 1 format topping out at 360p; "android_vr" exposed 29 up to 2160p.
        private const string FallbackPlayerClient = "android_vr";

        private string ExtractorArgs(string playerClient)
        {
            return string.IsNullOrWhiteSpace(playerClient)
                ? string.Empty
                : $" --extractor-args \"youtube:player_client={playerClient}\"";
        }

        // Points yt-dlp at the same ffmpeg the user configured. Accepts the binary path or its
        // directory; we pass the directory so a differently-named binary still resolves.
        private string GetFfmpegLocationArg()
        {
            var ffmpeg = _settings?.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
                return string.Empty;
            var dir = Path.GetDirectoryName(ffmpeg);
            return string.IsNullOrWhiteSpace(dir) ? string.Empty : $" --ffmpeg-location \"{dir}\"";
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

        // Resolution is the CONSTRAINT; codec is only a preference, expressed through -S below.
        //
        // These used to filter on [vcodec^=avc1] directly. yt-dlp's "/" means "if the left side
        // matches nothing, try the right" — and the left side always matched something, just at a
        // lower resolution. YouTube publishes avc1 only up to 1080p (1440p and 2160p are VP9/AV1
        // exclusively), so Best resolved to 1080p on every 4K video and never fell through to the
        // bv*+ba branch that would have found 2160p. The same trap fires one rung down wherever a
        // video has no avc1 at the requested height: the selector quietly settles for a smaller
        // avc1 rather than the right-sized VP9.
        private static string FormatSelector(VideoQuality q)
        {
            var cap = HeightCap(q);
            return cap == 0
                ? "bv*+ba/b"
                : $"bv*[height<={cap}]+ba/b[height<={cap}]";
        }

        // 0 == no cap (Best). Also used to tell the user when YouTube simply had nothing this tall.
        private static int HeightCap(VideoQuality q)
        {
            switch (q)
            {
                case VideoQuality.P2160: return 2160;
                case VideoQuality.P1440: return 1440;
                case VideoQuality.P1080: return 1080;
                case VideoQuality.P720: return 720;
                case VideoQuality.P480: return 480;
                default: return 0;
            }
        }

        public static string QualityLabel(VideoQuality q)
        {
            var cap = HeightCap(q);
            return cap == 0 ? "Best available" : cap + "p";
        }

        // Keeps the avc1/AAC preference that makes the remux path cheap, but as a SORT so it can
        // only decide between formats of equal resolution — it can no longer cost the user
        // resolution. res first, then fps, then codec.
        private const string FormatSort = "res,fps,vcodec:h264,acodec:aac";

        // -preset veryfast: 3-5x faster than the default (medium) for marginal size cost — a
        // trailer re-encode shouldn't take minutes. Only the fallback path pays this at all.
        private string BuildFfmpegArgs(string input, string output, string ffmpegPath)
        {
            var enc = ResolveVideoEncoder(ffmpegPath);
            return $"-y -i \"{input}\" -c:v {enc} {EncoderQualityArgs(enc)} -pix_fmt yuv420p -c:a aac " +
                   $"-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -movflags +faststart \"{output}\"";
        }

        // Now that Best genuinely means best, the common Best download is 4K VP9 (YouTube has no
        // H.264 above 1080p), which always takes the re-encode path — EML wants H.264. On software
        // libx264 that is minutes of CPU for a long trailer, so use the GPU when there is one.
        //
        // Detection is per-machine and happens at runtime — nothing about which GPU vendors are
        // viable is baked in here. Candidates are tried in nvenc -> amf -> qsv order so an NVIDIA
        // machine gets nvenc, an AMD machine amf, an Intel machine qsv.
        //
        // Each is proven with a real one-frame encode rather than trusted from `ffmpeg -encoders`,
        // because that list reports what was COMPILED IN, not what can run: a build routinely
        // advertises all three while the matching driver/runtime is absent on that particular box
        // (nvenc without nvcuda.dll, qsv without the Intel runtime). libx264 is always the backstop,
        // so a machine with no usable GPU encoder behaves exactly as before.
        private static string _resolvedEncoder;
        private static readonly object _encoderLock = new object();

        private string ResolveVideoEncoder(string ffmpegPath)
        {
            lock (_encoderLock)
            {
                if (_resolvedEncoder != null) return _resolvedEncoder;

                foreach (var candidate in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
                {
                    if (EncoderRuns(ffmpegPath, candidate))
                    {
                        _resolvedEncoder = candidate;
                        break;
                    }
                }
                if (_resolvedEncoder == null)
                    _resolvedEncoder = "libx264";

                Log("Video encoder: " + _resolvedEncoder +
                    (_resolvedEncoder == "libx264" ? " (software — no working GPU encoder found)" : " (hardware)"));
                return _resolvedEncoder;
            }
        }

        // One tiny synthetic frame to /dev/null. Cheap, and it fails fast on a missing runtime.
        private static bool EncoderRuns(string ffmpegPath, string encoder)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -loglevel error -f lavfi -i testsrc=size=256x256:rate=30:duration=0.1 " +
                                $"-c:v {encoder} -pix_fmt yuv420p -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // Per-encoder quality knobs. Hardware encoders default to noticeably worse output than
        // libx264 unless a rate-control target is given, so each gets an explicit one at roughly
        // matched visual quality rather than whatever the driver picks.
        private static string EncoderQualityArgs(string encoder)
        {
            switch (encoder)
            {
                case "h264_nvenc": return "-preset p5 -rc vbr -cq 21";
                case "h264_amf": return "-quality balanced -rc cqp -qp_i 21 -qp_p 21";
                case "h264_qsv": return "-preset medium -global_quality 21";
                default: return "-preset veryfast -crf 21";
            }
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

            // Drop unmerged per-format fragments. When a merge does not happen, yt-dlp leaves
            // "<stem>.f299.mp4" (video only) beside "<stem>.f140.m4a" (audio only) instead of one
            // muxed file. The old .mp4-first preference picked the video-only fragment and
            // happily packaged it, producing a silent trailer with no error anywhere. A merged
            // file never carries the .fNNN marker, so requiring its absence keeps only real output.
            var merged = new System.Collections.Generic.List<string>();
            foreach (var m in matches)
            {
                if (!Regex.IsMatch(Path.GetFileNameWithoutExtension(m), @"\.f\d+$"))
                    merged.Add(m);
            }
            if (merged.Count == 0)
                return null; // only fragments — treat as a failed download rather than ship silence

            foreach (var ext in new[] { ".mp4", ".mkv", ".webm" })
            {
                foreach (var m in merged)
                    if (m.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        return m;
            }
            return merged[0];
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

                            // yt-dlp prints "[info] <id>: Downloading 1 format(s): 299+140".
                            // Recording it turns "why is my trailer 720p?" into a one-line answer
                            // instead of a guessing game — the itags say exactly what was chosen.
                            if (line.IndexOf("Downloading 1 format(s)", StringComparison.OrdinalIgnoreCase) >= 0
                                || line.IndexOf("format(s):", StringComparison.OrdinalIgnoreCase) >= 0)
                                Log(line.Trim());

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
