using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace FullVid
{
    public class FullVid : GenericPlugin
    {
        private readonly IPlayniteAPI _api;

        public override Guid Id { get; } = Guid.Parse("fd1c93dc-92a4-4380-a090-47d68988eb0c");

        public bool IsFullscreen => _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
        public bool IsDesktop => _api.ApplicationInfo.Mode == ApplicationMode.Desktop;

        public FullVid(IPlayniteAPI api) : base(api)
        {
            _api = api;
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            return Enumerable.Empty<GameMenuItem>();
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return Enumerable.Empty<MainMenuItem>();
        }

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            return Enumerable.Empty<TopPanelItem>();
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            return Enumerable.Empty<SidebarItem>();
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return null;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return null;
        }
    }
}
