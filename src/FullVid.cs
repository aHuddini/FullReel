using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Windows.Controls;
using FullVid.Common;
using FullVid.Dialogs;
using FullVid.Models;
using FullVid.Services;
using FullVid.Services.Controller;

namespace FullVid
{
    public class FullVid : GenericPlugin
    {
        private readonly IPlayniteAPI _api;
        private FileLogger _fileLogger;
        private ControllerEventRouter _controllerRouter;
        private readonly FullVidSettingsViewModel _settingsViewModel;

        // Must equal the GUID in extension.yaml's Id (FullVid.087df234-...) — Playnite keys the
        // add-on's settings/registration off GenericPlugin.Id, so a mismatch hides the settings page.
        public override Guid Id { get; } = Guid.Parse("087df234-b55b-4824-a7a2-3adac1aec1ec");

        // Resolve bundled dependencies (MaterialDesignThemes.Wpf etc.) from the extension's own
        // folder when normal probing misses — XAML-triggered loads fail on portable Playnite
        // installs otherwise ("Could not load file or assembly"). Same fix UniPlaySong ships.
        static FullVid()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var name = new System.Reflection.AssemblyName(args.Name).Name;
                    var dir = System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (string.IsNullOrEmpty(dir))
                        return null;

                    var dll = System.IO.Path.Combine(dir, name + ".dll");
                    if (System.IO.File.Exists(dll))
                        return System.Reflection.Assembly.LoadFrom(dll);
                }
                catch
                {
                    // Never let a resolve attempt throw — returning null lets other resolvers try.
                }
                return null;
            };
        }

        public bool IsFullscreen => _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
        public bool IsDesktop => _api.ApplicationInfo.Mode == ApplicationMode.Desktop;

        public FullVid(IPlayniteAPI api) : base(api)
        {
            _api = api;
            _fileLogger = new FileLogger(GetPluginUserDataPath(), _api?.Paths?.ConfigurationPath);
            _controllerRouter = new ControllerEventRouter(_fileLogger);
            _settingsViewModel = new FullVidSettingsViewModel(this);

            // Without HasSettings, Playnite never surfaces the settings page even though
            // GetSettings/GetSettingsView are implemented — the add-on shows no settings entry.
            Properties = new GenericPluginProperties { HasSettings = true };

            // Register this instance so dialogs (DialogHelper) can reach the shared router.
            if (Application.Current?.Properties != null)
            {
                Application.Current.Properties[DialogHelper.PluginPropertyKey] = this;
            }
        }

        // Exposes the shared controller-event router for modal dialogs.
        public ControllerEventRouter GetControllerEventRouter() => _controllerRouter;

        // Exposes the file logger so dialogs can drop debug traces into FullVid.log.
        public FileLogger GetFileLogger() => _fileLogger;

        // Playnite treats the ViewModel as the ISettings object and sets it as the view's DataContext.
        public override ISettings GetSettings(bool firstRunSettings) => _settingsViewModel;

        public override UserControl GetSettingsView(bool firstRunSettings) => new FullVidSettingsView();

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var game = args?.Games?.FirstOrDefault();
            if (game == null)
                return Enumerable.Empty<GameMenuItem>();

            return new List<GameMenuItem>
            {
                new GameMenuItem
                {
                    // Single top-level "FullReel" entry — clicking it opens the video dialog directly
                    // (no submenu). Surfaces cleanly in both Desktop and Fullscreen game options.
                    Description = "FullReel",
                    Action = _ => FindVideos(game)
                }
            };
        }

        // Opens the results dialog immediately, then searches YouTube in the background and
        // pushes results into the open window — the UI never blocks on yt-dlp. Closing the
        // window cancels the in-flight search.
        private void FindVideos(Game game)
        {
            var settings = _settingsViewModel.Settings;

            var ytDlpPath = settings.YtDlpPath;
            if (string.IsNullOrWhiteSpace(ytDlpPath) || !System.IO.File.Exists(ytDlpPath))
            {
                _api.Dialogs.ShowErrorMessage(
                    "yt-dlp is not configured. Set its path in FullReel settings.",
                    "FullReel");
                return;
            }

            var categories = VideoCategory.Defaults;
            var count = settings.SearchResultCount;
            var gameName = game.Name ?? string.Empty;

            // Query for a category: Trailers (index 0) honours the user's QueryTemplate; the rest
            // use "{gameName} {suffix}".
            Func<VideoCategory, string> buildQuery = c =>
                c == categories[0]
                    ? (settings.QueryTemplate ?? string.Empty).Replace("{name}", gameName)
                    : (gameName + " " + c.QuerySuffix).Trim();

            // Only the newest search may push results — switching category cancels the prior one.
            CancellationTokenSource cts = null;
            // Per-category result cache for this session: a category searched once is retained,
            // so switching back to it is instant (no re-search).
            var cache = new System.Collections.Generic.Dictionary<int, List<VideoResult>>();

            Window window = null;
            VideoResultsDialog dialog = null;

            // Shows a category's results: instant from cache, else searches and caches on success.
            Action<int> runSearch = categoryIndex =>
            {
                cts?.Cancel();

                if (cache.TryGetValue(categoryIndex, out var cached))
                {
                    dialog.SetResults(cached); // already loaded this session — no re-search
                    return;
                }

                var myCts = new CancellationTokenSource();
                cts = myCts;

                var query = buildQuery(categories[categoryIndex]);
                dialog.SetStatus("Searching for \"" + query + "\"…");

                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var search = new YouTubeSearchService(ytDlpPath, settings.DenoPath);
                        var results = await search.SearchAsync(query, count, myCts.Token).ConfigureAwait(false);
                        if (!myCts.IsCancellationRequested)
                        {
                            // Cache only non-empty results — a failed/empty search stays retryable.
                            if (results != null && results.Count > 0)
                                cache[categoryIndex] = results;
                            dialog.SetResults(results);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _fileLogger?.Error("YouTube search failed: " + ex.Message);
                        if (!myCts.IsCancellationRequested)
                            dialog.SetStatus("Search failed. Check yt-dlp in settings.");
                    }
                });
            };

            // UPS stays paused for the WHOLE FullReel session: paused when the search dialog
            // opens, held through watch/download handoffs (no resume blip between windows), and
            // resumed only when the user is fully out.
            var bridge = new UniPlaySongBridge(new ProcessUriInvoker());
            var upsPaused = false;
            if (settings.PauseUniPlaySong)
            {
                bridge.Pause();
                upsPaused = true;
            }
            var handedOff = false; // true while a watch/download continues the session

            dialog = new VideoResultsDialog(
                _api,
                categories,
                onWatch: v =>
                {
                    handedOff = true;
                    window?.Close();
                    WatchVideo(game, v); // player pauses/resumes UPS around itself
                },
                onDownload: v =>
                {
                    handedOff = true;
                    window?.Close();
                    DownloadVideo(game, v);
                    if (upsPaused) bridge.Resume(); // download done — session over
                },
                onClose: () => window?.Close(),
                onCategoryChanged: runSearch);

            window = DialogHelper.CreateFullscreenDialog(_api, dialog, "FullReel", 900, 640, IsFullscreen);
            DialogHelper.AddFocusReturnHandler(window, _api, "FindVideos");
            window.Closed += (s, e) =>
            {
                cts?.Cancel();
                // Resume only when the session truly ends here (B/Esc on the results). Watch and
                // download paths resume via their own completion instead.
                if (upsPaused && !handedOff) bridge.Resume();
            };

            runSearch(0); // initial category: Trailers
            window.ShowDialog();
            cts?.Dispose();
        }

        // Opens the fullscreen WebView2 player for the chosen result. The bridge (default
        // ProcessUriInvoker) pauses/resumes UniPlaySong around playback; settings decide
        // whether that happens at all. Y in the player routes back through DownloadVideo.
        private void WatchVideo(Game game, VideoResult video)
        {
            var settings = _settingsViewModel.Settings;
            var bridge = new UniPlaySongBridge(new ProcessUriInvoker());

            Window window = null;
            var player = new VideoPlayerDialog(_api, video, settings, bridge,
                onDownload: v =>
                {
                    window?.Close();
                    DownloadVideo(game, v);
                });

            // Borderless (no Playnite title bar) — the player draws its own glass top/bottom bars
            // in-page, so window chrome would just be a mismatched duplicate strip.
            window = DialogHelper.CreateBorderlessDialog(_api, player, 1280, 760, IsFullscreen);
            DialogHelper.AddFocusReturnHandler(window, _api, "WatchVideo");
            window.ShowDialog();
        }

        // Downloads the chosen video, transcodes it to EML's format, and lands it as the
        // game's VideoTrailer.mp4. Runs under a cancelable global-progress dialog (mirrors
        // FindVideos). On success, tells the user to re-select the game — EML has no
        // filewatcher; it re-scans the trailer folder only when the game selection changes.
        private void DownloadVideo(Game game, VideoResult video)
        {
            var settings = _settingsViewModel.Settings;
            var service = new VideoDownloadService(
                settings,
                _api.Paths.ConfigurationPath,
                System.IO.Path.Combine(GetPluginUserDataPath(), "temp"),
                _fileLogger);

            var ok = false;
            _api.Dialogs.ActivateGlobalProgress(gp =>
            {
                var progress = new Progress<string>(text => gp.Text = text);
                try
                {
                    ok = service.DownloadAsync(video, game, progress, gp.CancelToken).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _fileLogger?.Error("Download failed: " + ex.Message);
                }
            }, new GlobalProgressOptions("Downloading trailer...") { Cancelable = true, IsIndeterminate = true });

            if (ok)
            {
                _api.Dialogs.ShowMessage(
                    "Trailer downloaded for \"" + (game.Name ?? "game") + "\".\n\n" +
                    "Re-select the game so ExtraMetadataLoader picks up the new trailer.",
                    "FullReel");
            }
            else
            {
                _api.Dialogs.ShowErrorMessage(
                    "Trailer download failed. Check that yt-dlp and FFmpeg are configured in FullReel settings.",
                    "FullReel");
            }
        }

        // Fullscreen-mode controller input. State semantics match UniPlaySong's RouteControllerInput:
        // ControllerInputState.Pressed = button down, ControllerInputState.Released = button up.
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            if (args == null) return;
            if (args.State == ControllerInputState.Pressed)
                _controllerRouter.HandleButtonPressed(args.Button);
            else if (args.State == ControllerInputState.Released)
                _controllerRouter.HandleButtonReleased(args.Button);
        }

        // Desktop-mode controller input (same routing as fullscreen).
        public override void OnDesktopControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            if (args == null) return;
            if (args.State == ControllerInputState.Pressed)
                _controllerRouter.HandleButtonPressed(args.Button);
            else if (args.State == ControllerInputState.Released)
                _controllerRouter.HandleButtonReleased(args.Button);
        }
    }
}
