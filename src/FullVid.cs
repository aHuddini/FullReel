using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Windows.Controls;
using FullVid.Common;
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
            return Enumerable.Empty<GameMenuItem>();
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
