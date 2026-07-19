using System.Collections.Generic;

namespace FullVid
{
    // How yt-dlp should source YouTube cookies (speeds previews/downloads via login).
    public enum CookieMode
    {
        None,       // No cookies
        Firefox,    // --cookies-from-browser firefox
        Chrome,     // --cookies-from-browser chrome
        Edge,       // --cookies-from-browser edge
        Brave,      // --cookies-from-browser brave
        Opera,      // --cookies-from-browser opera
        CustomFile  // --cookies <netscape-file>
    }

    // Target video quality for trailer downloads.
    public enum VideoQuality
    {
        Best,
        P1080,
        P720,
        P480
    }

    // How the player's controls hint bar is rendered. All except Performance are in-page CSS
    // glass variants (backdrop-filter over the live video — GPU-native, near-zero cost).
    public enum PlayerBarStyle
    {
        FrostedBlur,   // official look: neutral frosted glass, moderate blur
        HeavyFrost,    // thicker iOS-style glass: strong blur, opaque white-ish frost
        TintedPurple,  // accent-tinted glass matching the DeepPurple theme
        MinimalGlass,  // barely-there: light blur, faint tint, mostly video shows through
        GradientFade,  // no hard edge: dark bottom-up gradient + light blur (YouTube/Netflix-style)
        Performance    // plain WPF strip below the video — no blur, no overlay (cheapest)
    }

    // Persisted plugin settings. Plain ObservableObject; the ISettings lifecycle
    // (BeginEdit/EndEdit/CancelEdit/VerifySettings) lives on FullVidSettingsViewModel,
    // which Playnite treats as the settings object (GetSettings) and sets as DataContext.
    public class FullVidSettings : ObservableObject
    {
        private string ytDlpPath = string.Empty;
        public string YtDlpPath { get => ytDlpPath; set { ytDlpPath = value ?? string.Empty; OnPropertyChanged(); } }

        private string ffmpegPath = string.Empty;
        public string FfmpegPath { get => ffmpegPath; set { ffmpegPath = value ?? string.Empty; OnPropertyChanged(); } }

        // deno.exe — yt-dlp's external JS runtime for YouTube nsig/PO-token challenges. When set,
        // its folder is prepended to the yt-dlp process PATH so yt-dlp discovers it.
        private string denoPath = string.Empty;
        public string DenoPath { get => denoPath; set { denoPath = value ?? string.Empty; OnPropertyChanged(); } }

        // FrostedBlur = glass bar over the video (live blurred backing). Performance = plain strip
        // below the video, no blur — lighter on the GPU.
        private PlayerBarStyle playerBarStyle = PlayerBarStyle.FrostedBlur;
        public PlayerBarStyle PlayerBarStyle { get => playerBarStyle; set { playerBarStyle = value; OnPropertyChanged(); } }

        // When the player is expanded to fullscreen: false (default) keeps the bottom controls
        // bar visible; true auto-hides it after a few seconds. RB/P still toggles it live either
        // way — this only seeds the default.
        private bool fullscreenBarAutoHide = false;
        public bool FullscreenBarAutoHide { get => fullscreenBarAutoHide; set { fullscreenBarAutoHide = value; OnPropertyChanged(); } }

        // Ask the embed for HD via YouTube's internal player API (the official quality APIs are
        // dead no-ops). Fully fail-soft — if YouTube changes internals or bandwidth can't keep
        // up, playback falls back to normal adaptive quality. Kill switch for when it misbehaves.
        private bool forceHdPlayback = true;
        public bool ForceHdPlayback { get => forceHdPlayback; set { forceHdPlayback = value; OnPropertyChanged(); } }

        private int searchResultCount = 10;
        // Clamp 1..50 in the setter — the settings TextBox is unvalidated, and a huge value goes
        // straight into yt-dlp's ytsearch{n}: (a slow, unbounded search). Covers every entry path.
        public int SearchResultCount
        {
            get => searchResultCount;
            set { searchResultCount = System.Math.Max(1, System.Math.Min(50, value)); OnPropertyChanged(); }
        }

        private string queryTemplate = "{name} gameplay trailer";
        public string QueryTemplate { get => queryTemplate; set { queryTemplate = value ?? string.Empty; OnPropertyChanged(); } }

        private bool pauseUniPlaySong = true;
        public bool PauseUniPlaySong { get => pauseUniPlaySong; set { pauseUniPlaySong = value; OnPropertyChanged(); } }

        private VideoQuality downloadQuality = VideoQuality.Best;
        public VideoQuality DownloadQuality { get => downloadQuality; set { downloadQuality = value; OnPropertyChanged(); } }

        private CookieMode cookiesSource = CookieMode.None;
        public CookieMode CookiesSource { get => cookiesSource; set { cookiesSource = value; OnPropertyChanged(); } }

        private string customCookiesFilePath = string.Empty;
        public string CustomCookiesFilePath { get => customCookiesFilePath; set { customCookiesFilePath = value ?? string.Empty; OnPropertyChanged(); } }

        private bool enableDebugLogging = false;
        public bool EnableDebugLogging { get => enableDebugLogging; set { enableDebugLogging = value; OnPropertyChanged(); } }
    }
}
