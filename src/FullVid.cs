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

        public override Guid Id { get; } = Guid.Parse("fd1c93dc-92a4-4380-a090-47d68988eb0c");

        public bool IsFullscreen => _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
        public bool IsDesktop => _api.ApplicationInfo.Mode == ApplicationMode.Desktop;

        public FullVid(IPlayniteAPI api) : base(api)
        {
            _api = api;
            _fileLogger = new FileLogger(GetPluginUserDataPath(), _api?.Paths?.ConfigurationPath);
            _controllerRouter = new ControllerEventRouter(_fileLogger);
            _settingsViewModel = new FullVidSettingsViewModel(this);

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
                    Description = "FullVid: Find Videos",
                    Action = _ => FindVideos(game)
                }
            };
        }

        // Search YouTube for the game, then show the controller-navigable results dialog.
        // Watch/Download are stubbed here (Tasks 6/7 wire the real player/download).
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
                    DialogHelper.ShowControllerConfirmation(_api,
                        "Watch \"" + v.Title + "\"?\n\nPlayback arrives in the next update.",
                        "FullVid");
                },
                onDownload: v =>
                {
                    window?.Close();
                    DialogHelper.ShowControllerConfirmation(_api,
                        "Download \"" + v.Title + "\"?\n\nDownloading arrives in the next update.",
                        "FullVid");
                },
                onClose: () => window?.Close());

            window = DialogHelper.CreateFullscreenDialog(_api, dialog, "FullVid: Find Videos", 760, 640, IsFullscreen);
            DialogHelper.AddFocusReturnHandler(window, _api, "FindVideos");
            window.ShowDialog();
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
