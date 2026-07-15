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
                    // MenuSection groups it under a named "FullVid" submenu — the same shape UPS
                    // uses, which is what Fullscreen surfaces as a navigable game-options entry.
                    Description = "Find Videos",
                    MenuSection = "FullVid",
                    Action = _ => FindVideos(game)
                }
            };
        }

        // Search YouTube for the game, then show the controller-navigable results dialog.
        private void FindVideos(Game game)
        {
            var settings = _settingsViewModel.Settings;

            var ytDlpPath = settings.YtDlpPath;
            if (string.IsNullOrWhiteSpace(ytDlpPath) || !System.IO.File.Exists(ytDlpPath))
            {
                _api.Dialogs.ShowErrorMessage(
                    "yt-dlp is not configured. Set its path in FullVid settings.",
                    "FullVid");
                return;
            }

            var query = (settings.QueryTemplate ?? string.Empty).Replace("{name}", game.Name ?? string.Empty);
            var count = settings.SearchResultCount;

            List<VideoResult> results = null;
            _api.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = "Searching YouTube for \"" + query + "\"...";
                var search = new YouTubeSearchService(ytDlpPath);
                try
                {
                    results = search.SearchAsync(query, count, progress.CancelToken).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _fileLogger?.Error("YouTube search failed: " + ex.Message);
                }
            }, new GlobalProgressOptions("Searching...") { Cancelable = true, IsIndeterminate = true });

            if (results == null || results.Count == 0)
            {
                _api.Dialogs.ShowMessage(
                    "No videos found for \"" + query + "\".",
                    "FullVid");
                return;
            }

            Window window = null;
            var dialog = new VideoResultsDialog(
                _api,
                results,
                onWatch: v =>
                {
                    window?.Close();
                    WatchVideo(game, v);
                },
                onDownload: v =>
                {
                    window?.Close();
                    DownloadVideo(game, v);
                },
                onClose: () => window?.Close());

            window = DialogHelper.CreateFullscreenDialog(_api, dialog, "FullVid: Find Videos", 760, 640, IsFullscreen);
            DialogHelper.AddFocusReturnHandler(window, _api, "FindVideos");
            window.ShowDialog();
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

            window = DialogHelper.CreateFullscreenDialog(_api, player, "FullVid: " + (video?.Title ?? "Player"), 1280, 760, IsFullscreen);
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
                    "FullVid");
            }
            else
            {
                _api.Dialogs.ShowErrorMessage(
                    "Trailer download failed. Check that yt-dlp and FFmpeg are configured in FullVid settings.",
                    "FullVid");
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
