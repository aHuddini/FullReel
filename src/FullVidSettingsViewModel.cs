using System.Collections.Generic;
using System.Windows.Input;
using FullVid.Common;
using Playnite.SDK;

namespace FullVid
{
    // The settings object Playnite gets from FullVid.GetSettings and sets as the view's
    // DataContext. Wraps the persisted FullVidSettings model, owns the ISettings lifecycle,
    // and exposes tool-path validation status + Browse commands for the settings view.
    public class FullVidSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FullVid plugin;
        private readonly ToolProbe _probe = new ToolProbe();
        private FullVidSettings settings;

        public FullVidSettingsViewModel(FullVid plugin)
        {
            this.plugin = plugin;
            settings = plugin.LoadPluginSettings<FullVidSettings>() ?? new FullVidSettings();
        }

        public FullVidSettings Settings
        {
            get => settings;
            set { settings = value; OnPropertyChanged(); }
        }

        private string _ytDlpStatus = string.Empty;
        public string YtDlpStatus { get => _ytDlpStatus; set { _ytDlpStatus = value; OnPropertyChanged(); } }

        private string _ffmpegStatus = string.Empty;
        public string FfmpegStatus { get => _ffmpegStatus; set { _ffmpegStatus = value; OnPropertyChanged(); } }

        private string _denoStatus = string.Empty;
        public string DenoStatus { get => _denoStatus; set { _denoStatus = value; OnPropertyChanged(); } }

        // Player-bar style bound to the settings dropdown. Wraps Settings.PlayerBarStyle so
        // changing it also refreshes the preview image below the dropdown.
        public PlayerBarStyle SelectedBarStyle
        {
            get => settings.PlayerBarStyle;
            set
            {
                if (settings.PlayerBarStyle == value) return;
                settings.PlayerBarStyle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BarStylePreviewPath));
            }
        }

        // Pack URI of the bundled preview PNG for the selected style. Performance has no glass
        // preview — falls back to the official FrostedBlur image.
        public string BarStylePreviewPath
        {
            get
            {
                var name = settings.PlayerBarStyle == PlayerBarStyle.Performance
                    ? "FrostedBlur" : settings.PlayerBarStyle.ToString();
                return "pack://application:,,,/FullVid;component/Images/StylePreviews/" + name + ".png";
            }
        }

        public ICommand BrowseYtDlp => new RelayCommand(() =>
        {
            var path = plugin.PlayniteApi.Dialogs.SelectFile("yt-dlp|yt-dlp.exe|Executable|*.exe");
            if (!string.IsNullOrWhiteSpace(path))
            {
                settings.YtDlpPath = path;
                UpdateToolStatus();
            }
        });

        public ICommand BrowseFfmpeg => new RelayCommand(() =>
        {
            var path = plugin.PlayniteApi.Dialogs.SelectFile("ffmpeg|ffmpeg.exe|Executable|*.exe");
            if (!string.IsNullOrWhiteSpace(path))
            {
                settings.FfmpegPath = path;
                UpdateToolStatus();
            }
        });

        public ICommand BrowseDeno => new RelayCommand(() =>
        {
            var path = plugin.PlayniteApi.Dialogs.SelectFile("deno|deno.exe|Executable|*.exe");
            if (!string.IsNullOrWhiteSpace(path))
            {
                settings.DenoPath = path;
                UpdateToolStatus();
            }
        });

        public ICommand BrowseCookiesFile => new RelayCommand(() =>
        {
            var path = plugin.PlayniteApi.Dialogs.SelectFile("Cookies file|*.txt|All files|*.*");
            if (!string.IsNullOrWhiteSpace(path))
                settings.CustomCookiesFilePath = path;
        });

        // Re-probe both tools and refresh the status strings. Called on settings open
        // (BeginEdit) and after each Browse. Cheap: ToolProbe caches by path+mtime.
        private void UpdateToolStatus()
        {
            YtDlpStatus = _probe.Probe(settings.YtDlpPath, "--version");
            // ffmpeg needs -version (single dash): --version exits non-zero and it prints the
            // banner to stderr. Verified against ffmpeg 8.0.1 (gyan build): -version → exit 0,
            // stdout. deno also uses --version.
            FfmpegStatus = _probe.Probe(settings.FfmpegPath, "-version");
            // deno prints "deno 1.x.x" on the first --version line; ToolProbe takes the first line.
            DenoStatus = _probe.Probe(settings.DenoPath, "--version");
        }

        // ISettings lifecycle. Playnite calls BeginEdit when the view opens.
        public void BeginEdit()
        {
            UpdateToolStatus();
        }

        public void CancelEdit()
        {
            settings = plugin.LoadPluginSettings<FullVidSettings>() ?? new FullVidSettings();
            OnPropertyChanged(nameof(Settings));
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}
