using System;
using System.Windows;
using System.Windows.Controls;
using FullVid.Common;
using FullVid.Models;
using FullVid.Services;
using FullVid.Services.Controller;
using Microsoft.Web.WebView2.Core;
using Playnite.SDK;
using Playnite.SDK.Events;

namespace FullVid.Dialogs
{
    // What a controller button press resolves to in the player.
    public enum PlayerAction
    {
        None,
        PlayPause,
        Close,
        SeekForward,
        SeekBackward,
        VolumeUp,
        VolumeDown,
        Download
    }

    // Fullscreen WebView2 YouTube player. Transport is driven entirely from C#
    // (ExecuteScriptAsync against the YT IFrame Player API) — the embedded page's own
    // controls are hidden. A = play/pause, B = close, D-pad = seek/volume, Y = download.
    // Pauses UniPlaySong's game music on open and resumes it on the window's Closed event
    // (fires on EVERY close path, so UPS is never left stuck paused).
    public partial class VideoPlayerDialog : UserControl, IControllerInputReceiver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // D-pad debounce: XInput + WPF can both fire for one physical press. 150ms gate
        // (ported from VideoResultsDialog) drops the duplicate so seek/volume don't repeat.
        private DateTime _lastDpadTime = DateTime.MinValue;
        private const int DpadDebounceMs = 150;

        private readonly IPlayniteAPI _api;
        private readonly FullVidSettings _settings;
        private readonly UniPlaySongBridge _bridge;
        private readonly VideoResult _video;
        private readonly Action<VideoResult> _onDownload;
        private readonly bool _swapAB;

        // Guard so we only Resume() UPS if we actually Paused() it (setting was on).
        private bool _upsPaused;
        private bool _webReady;

        // FrostedBlur bar style: the hint bar lives INSIDE the hosted page as an HTML overlay
        // with CSS backdrop-filter — Chromium blurs its own video on the GPU. WPF does nothing.
        private bool _frosted;

        // Pure button-decision seam. swapAB models Playnite's SwapConfirmCancelButtons:
        // when true, A and B trade their PlayPause(confirm)/Close(cancel) roles. Y and the
        // D-pad are unaffected by the swap.
        public static PlayerAction Decide(ControllerInput button, bool swapAB)
        {
            var confirm = swapAB ? ControllerInput.B : ControllerInput.A;
            var cancel = swapAB ? ControllerInput.A : ControllerInput.B;

            if (button == confirm) return PlayerAction.PlayPause;
            if (button == cancel) return PlayerAction.Close;
            if (button == ControllerInput.DPadRight) return PlayerAction.SeekForward;
            if (button == ControllerInput.DPadLeft) return PlayerAction.SeekBackward;
            if (button == ControllerInput.DPadUp) return PlayerAction.VolumeUp;
            if (button == ControllerInput.DPadDown) return PlayerAction.VolumeDown;
            if (button == ControllerInput.Y) return PlayerAction.Download;

            return PlayerAction.None;
        }

        public VideoPlayerDialog(
            IPlayniteAPI api,
            VideoResult video,
            FullVidSettings settings,
            UniPlaySongBridge bridge,
            Action<VideoResult> onDownload = null)
        {
            InitializeComponent();

            _api = api;
            _video = video;
            _settings = settings;
            _bridge = bridge;
            _onDownload = onDownload;

            // Read the confirm/cancel swap once, up front. Read-only SDK property; default
            // to un-swapped on any failure so A always confirms in the worst case.
            try { _swapAB = api?.ApplicationSettings?.Fullscreen?.SwapConfirmCancelButtons ?? false; }
            catch { _swapAB = false; }

            Loaded += OnDialogLoaded;
            Unloaded += OnDialogUnloaded;
        }

        private async void OnDialogLoaded(object sender, RoutedEventArgs e)
        {
            GetRouter()?.Register(this);

            SetupHintBar();

            // Keyboard parity. Wire on the hosting window (tunneling PreviewKeyDown) so keys
            // are caught before WebView2 focus swallows them.
            var window = Window.GetWindow(this);
            if (window != null)
                window.PreviewKeyDown += OnPreviewKeyDown;

            // Resume UPS on EVERY close path (B, window X, error, playback-ended) — wired to
            // the hosting window's Closed, not the B handler, so UPS is never left paused.
            if (window != null)
                window.Closed += OnWindowClosed;

            // Pause UniPlaySong here (not in the ctor) so pause and the resume-wire above are
            // atomic — a control that's constructed but never Loaded can't leave UPS stuck paused.
            if (_settings?.PauseUniPlaySong == true)
            {
                _bridge?.Pause();
                _upsPaused = true;
            }

            try
            {
                // --autoplay-policy=no-user-gesture-required so the picked video starts on load
                // instead of waiting for an A/Space press (WebView2 blocks autoplay by default).
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var env = await CoreWebView2Environment.CreateAsync(null, null, options);
                await Web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WebView2 runtime unavailable");
                _api?.Dialogs?.ShowErrorMessage(
                    "The WebView2 runtime is not installed, so video playback is unavailable.\n\n" +
                    "Install the Microsoft Edge WebView2 Evergreen runtime, then try again.",
                    "FullVid");
                window?.Close();
                return;
            }

            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // Serve our own HTML page from a virtual https host. Navigating straight to
            // youtube.com/embed/... loads it as a top-level page with an opaque origin, which
            // YouTube's embed rejects ("error 153 / player configuration"). Instead we host a
            // page under a real https origin and let the IFrame Player API create the iframe —
            // the API then passes a valid origin/referrer and the embed plays.
            Web.CoreWebView2.AddWebResourceRequestedFilter(PlayerPageUrl, CoreWebView2WebResourceContext.All);
            Web.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            Web.CoreWebView2.Navigate(PlayerPageUrl);
        }

        // Choose the hint-bar style from settings. FrostedBlur = HTML overlay inside the page
        // (BuildPlayerHtml includes it); Performance = plain WPF strip below the video.
        private void SetupHintBar()
        {
            _frosted = _settings?.PlayerBarStyle != PlayerBarStyle.Performance;

            PerfBar.Visibility = _frosted ? Visibility.Collapsed : Visibility.Visible;
            PerfBarRow.Height = _frosted ? new GridLength(0) : GridLength.Auto;
        }

        // Virtual origin for our hosted player page. A real https host name is what makes
        // YouTube accept the embed; the path is arbitrary.
        private const string PlayerHost = "fullvid.player";
        private const string PlayerPageUrl = "https://" + PlayerHost + "/player.html";

        // Answer the navigation to PlayerPageUrl with an in-memory HTML page that loads the
        // YouTube IFrame Player API and builds the player for this video id. No temp files.
        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!string.Equals(e.Request?.Uri, PlayerPageUrl, StringComparison.OrdinalIgnoreCase))
                return;

            var html = BuildPlayerHtml(_video?.Id ?? string.Empty, _video?.Title ?? string.Empty, _frosted);
            var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
            var env = Web.CoreWebView2.Environment;
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
        }

        // The hosted page: an IFrame Player API player filling the window, no native controls.
        // autoplay=1, controls=0 — all transport is driven from C# via ExecuteScriptAsync.
        // frostedBar adds the controls-hint legend as an in-page overlay with backdrop-filter:
        // Chromium blurs the live video behind it natively on the GPU — no WPF snapshots.
        private static string BuildPlayerHtml(string videoId, string title, bool frostedBar)
        {
            var safeId = System.Text.RegularExpressions.Regex.Replace(videoId ?? string.Empty, "[^A-Za-z0-9_-]", "");

            // Both bars are pointer-events:none — display-only; all input stays in C#. Both blur
            // the live video behind them (backdrop-filter, GPU-native) over a dark tint, so the
            // player reads consistently top-and-bottom as frosted glass.
            // Top bar auto-hides during playback (slides up) and reappears on any input — see the
            // fvShowTop() JS and OnControllerButtonPressed/keyboard poke below. So the title
            // doesn't permanently cover the top of the video.
            var topBar = !frostedBar ? "" :
                "<div id=\"tbar\" style=\"position:fixed;left:0;right:0;top:0;z-index:2147483647;" +
                "pointer-events:none;padding:12px 18px;box-sizing:border-box;" +
                "font:600 16px 'Segoe UI',sans-serif;color:#FFFFFF;" +
                "white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" +
                "background:rgba(10,10,10,.72);border-bottom:1px solid rgba(255,255,255,.15);" +
                "backdrop-filter:blur(18px) saturate(1.2);-webkit-backdrop-filter:blur(18px) saturate(1.2);" +
                "transition:transform .35s ease,opacity .35s ease;\">" +
                HtmlEscape(title) +
                "</div>";

            var bottomBar = !frostedBar ? "" :
                "<div style=\"position:fixed;left:0;right:0;bottom:0;z-index:2147483647;" +
                "pointer-events:none;padding:13px 8px;text-align:center;" +
                "font:14px 'Segoe UI',sans-serif;color:#F5F5F5;" +
                "background:rgba(18,18,18,.35);border-top:1px solid rgba(255,255,255,.25);" +
                "backdrop-filter:blur(18px) saturate(1.2);-webkit-backdrop-filter:blur(18px) saturate(1.2);\">" +
                "<b style=\"color:#B39DDB\">A / Space</b> Play/Pause" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">◄ ►</b> Seek 10s" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">▲ ▼</b> Volume" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">Y / D</b> Download" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#EF9A9A\">B / Esc</b> Close" +
                "</div>";

            return
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#p{width:100%;height:100%}</style>" +
                "</head><body><div id=\"p\"></div>" + topBar + bottomBar +
                "<script>" +
                "var player;" +
                "function onYouTubeIframeAPIReady(){" +
                "  player=new YT.Player('p',{videoId:'" + safeId + "'," +
                "    playerVars:{autoplay:1,controls:0,modestbranding:1,rel:0,playsinline:1}," +
                // Hide the top bar only once the video actually starts PLAYING (state 1), not on a
                // blind load timer — so it's clearly visible while the video buffers.
                "    events:{onStateChange:function(e){if(e.data===1)fvHideTopSoon();}}});" +
                "}" +
                "var s=document.createElement('script');s.src='https://www.youtube.com/iframe_api';" +
                "document.head.appendChild(s);" +
                // Auto-hiding top bar. fvShowTop() reveals it (C# pokes this on any input);
                // fvHideTopSoon() slides it up after 4s. Starts visible.
                "var _tt;var _tb=function(){return document.getElementById('tbar');};" +
                "window.fvHideTopSoon=function(){clearTimeout(_tt);_tt=setTimeout(function(){" +
                "var b=_tb();if(b){b.style.transform='translateY(-100%)';b.style.opacity='0';}},4000);};" +
                "window.fvShowTop=function(){var b=_tb();if(!b)return;" +
                "b.style.transform='translateY(0)';b.style.opacity='1';fvHideTopSoon();};" +
                "</script></body></html>";
        }

        // Minimal HTML-entity escape for the video title (untrusted — comes from yt-dlp JSON).
        private static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private void OnDialogUnloaded(object sender, RoutedEventArgs e)
        {
            GetRouter()?.Unregister(this);
        }

        // On the hosting window's Closed — the single choke point for every close path.
        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (sender is Window w)
            {
                w.Closed -= OnWindowClosed;
                w.PreviewKeyDown -= OnPreviewKeyDown;
            }

            if (_upsPaused)
            {
                _bridge?.Resume();
                _upsPaused = false;
            }

            // Dispose WebView2 or its msedgewebview2.exe host lingers until GC. Unhook the
            // nav handler first; Dispose can throw if the core never initialized — swallow it.
            try
            {
                if (Web?.CoreWebView2 != null)
                {
                    Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    Web.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                }
                Web?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error disposing WebView2");
            }
        }

        // Bootstrap the YT IFrame Player API so `player.playVideo()` etc. work. The embed
        // The hosted page (BuildPlayerHtml) builds `window.player` itself via
        // onYouTubeIframeAPIReady, so navigation-completed just marks transport ready.
        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webReady = e.IsSuccess;
        }

        private ControllerEventRouter GetRouter()
        {
            if (Application.Current?.Properties?.Contains(DialogHelper.PluginPropertyKey) == true)
            {
                var plugin = Application.Current.Properties[DialogHelper.PluginPropertyKey] as FullVid;
                return plugin?.GetControllerEventRouter();
            }
            return null;
        }

        public void OnControllerButtonPressed(ControllerInput button)
        {
            try
            {
                DispatchAction(Decide(button, _swapAB), keyboard: false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling controller input in VideoPlayerDialog");
            }
        }

        public void OnControllerButtonReleased(ControllerInput button) { }

        // Keyboard parity with the controller: Space/K=play/pause, Esc=close, Left/Right=seek,
        // Up/Down=volume, D=download. Routes through the same PlayerAction dispatch. keyboard=true
        // skips the seek/volume debounce (that gate is for the XInput+WPF double-fire only).
        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            PlayerAction action;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Space:
                case System.Windows.Input.Key.K: action = PlayerAction.PlayPause; break;
                case System.Windows.Input.Key.Escape: action = PlayerAction.Close; break;
                case System.Windows.Input.Key.Right: action = PlayerAction.SeekForward; break;
                case System.Windows.Input.Key.Left: action = PlayerAction.SeekBackward; break;
                case System.Windows.Input.Key.Up: action = PlayerAction.VolumeUp; break;
                case System.Windows.Input.Key.Down: action = PlayerAction.VolumeDown; break;
                case System.Windows.Input.Key.D: action = PlayerAction.Download; break;
                default: return;
            }

            e.Handled = true;
            DispatchAction(action, keyboard: true);
        }

        private void DispatchAction(PlayerAction action, bool keyboard)
        {
            // Any input reveals the auto-hiding top title bar and resets its 3s hide timer.
            Script("if(window.fvShowTop)fvShowTop();");

            switch (action)
            {
                case PlayerAction.PlayPause:
                    // Toggle via the player's own state so one press flips play<->pause.
                    Script("if(window.player){var s=player.getPlayerState&&player.getPlayerState();" +
                           "if(s===1){player.pauseVideo();}else{player.playVideo();}}");
                    break;
                case PlayerAction.SeekForward:
                    if (keyboard || TryDpad()) Script("if(window.player){player.seekTo(player.getCurrentTime()+10,true);}");
                    break;
                case PlayerAction.SeekBackward:
                    if (keyboard || TryDpad()) Script("if(window.player){player.seekTo(Math.max(0,player.getCurrentTime()-10),true);}");
                    break;
                case PlayerAction.VolumeUp:
                    if (keyboard || TryDpad()) Script("if(window.player){player.setVolume(Math.min(100,player.getVolume()+10));}");
                    break;
                case PlayerAction.VolumeDown:
                    if (keyboard || TryDpad()) Script("if(window.player){player.setVolume(Math.max(0,player.getVolume()-10));}");
                    break;
                case PlayerAction.Download:
                    // Hand off to the caller's download flow (which closes the player).
                    _onDownload?.Invoke(_video);
                    break;
                case PlayerAction.Close:
                    Window.GetWindow(this)?.Close();
                    break;
            }
        }

        // Fire a transport script against the YT IFrame API. No-op until the CoreWebView2
        // navigation has completed.
        private void Script(string js)
        {
            if (!_webReady || Web?.CoreWebView2 == null) return;
            _ = Web.CoreWebView2.ExecuteScriptAsync(js);
        }

        // 150ms gate so a D-pad press doesn't seek/volume twice (XInput + WPF).
        private bool TryDpad()
        {
            var now = DateTime.Now;
            if ((now - _lastDpadTime).TotalMilliseconds < DpadDebounceMs)
                return false;
            _lastDpadTime = now;
            return true;
        }
    }
}
