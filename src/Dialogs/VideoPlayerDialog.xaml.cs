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
        Download,
        ToggleFullscreen,  // Select / F — expand the player to fill the screen and back
        Screenshot         // RB / P — save a PNG of the current video frame
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
            if (button == ControllerInput.Back) return PlayerAction.ToggleFullscreen;
            if (button == ControllerInput.RightShoulder) return PlayerAction.Screenshot;

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

            // Keyboard arrives on TWO paths, deduped in DispatchKeyboardAction: the in-page
            // capture (works while the web view has focus, and preventDefaults YouTube's own
            // keys) and this WPF PreviewKeyDown (works when focus is anywhere else — e.g. after
            // alt-tab, when WebView2 never regains its inner HWND focus). Either alone proved
            // fragile: web-only died after alt-tab; both without dedupe double-fired toggles.
            var window = Window.GetWindow(this);
            if (window != null)
                window.PreviewKeyDown += OnPreviewKeyDown;

            // TEMP [KbdDiag]: trace every input/focus layer so we can see exactly which one dies
            // after alt-tab. Remove once the focus issue is settled.
            if (window != null)
            {
                window.Deactivated += (s2, e2) => Logger.Info("[KbdDiag] window Deactivated");
                window.GotKeyboardFocus += (s2, e2) =>
                    Logger.Info("[KbdDiag] window GotKeyboardFocus new=" + (e2.NewFocus?.GetType().Name ?? "null"));
                window.LostKeyboardFocus += (s2, e2) =>
                    Logger.Info("[KbdDiag] window LostKeyboardFocus old=" + (e2.OldFocus?.GetType().Name ?? "null"));
            }
            Web.GotFocus += (s2, e2) => Logger.Info("[KbdDiag] WebView2 GotFocus");
            Web.LostFocus += (s2, e2) => Logger.Info("[KbdDiag] WebView2 LostFocus");

            // Resume UPS on EVERY close path (B, window X, error, playback-ended) — wired to
            // the hosting window's Closed, not the B handler, so UPS is never left paused.
            if (window != null)
            {
                window.Closed += OnWindowClosed;
                // Alt-tab away/back reactivates the window but does NOT return focus to the
                // WebView2 HWND — and the in-page capture is the only keyboard path, so keys
                // would be dead. Push focus back into the web view on every (re)activation.
                window.Activated += OnWindowActivated;
            }

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
                    "FullReel");
                window?.Close();
                return;
            }

            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            // Keys captured in-page (see the keydown listener in BuildPlayerHtml) arrive here as
            // "key:<Key>". This is the reliable keyboard path once focus is inside the WebView2.
            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Serve our own HTML page from a virtual https host. Navigating straight to
            // youtube.com/embed/... loads it as a top-level page with an opaque origin, which
            // YouTube's embed rejects ("error 153 / player configuration"). Instead we host a
            // page under a real https origin and let the IFrame Player API create the iframe —
            // the API then passes a valid origin/referrer and the embed plays.
            Web.CoreWebView2.AddWebResourceRequestedFilter(PlayerPageUrl, CoreWebView2WebResourceContext.All);
            Web.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            Web.CoreWebView2.Navigate(PlayerPageUrl);
            // Put keyboard focus in the web page from the start (Win32-level — see FocusWebView).
            FocusWebView("initial");
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

            var style = _settings?.PlayerBarStyle ?? PlayerBarStyle.FrostedBlur;
            var html = BuildPlayerHtml(_video?.Id ?? string.Empty, _video?.Title ?? string.Empty, style, _frosted);
            var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
            var env = Web.CoreWebView2.Environment;
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
        }

        // The hosted page: an IFrame Player API player filling the window, no native controls.
        // autoplay=1, controls=0 — all transport is driven from C# via ExecuteScriptAsync.
        // Per-style glass skin for the bars. All are in-page CSS (backdrop-filter over the live
        // video). Returns (topBackground, bottomBackground, blurPx, topBorderCss, bottomBorderCss).
        private static void GetBarSkin(PlayerBarStyle style, out string topBg, out string botBg,
            out int blur, out string topBorder, out string botBorder)
        {
            switch (style)
            {
                case PlayerBarStyle.HeavyFrost:
                    topBg = "rgba(40,40,44,.55)"; botBg = "rgba(40,40,44,.5)"; blur = 28;
                    topBorder = "1px solid rgba(255,255,255,.35)"; botBorder = "1px solid rgba(255,255,255,.35)";
                    break;
                case PlayerBarStyle.TintedPurple:
                    topBg = "rgba(52,34,78,.62)"; botBg = "rgba(52,34,78,.5)"; blur = 18;
                    topBorder = "1px solid rgba(179,157,219,.4)"; botBorder = "1px solid rgba(179,157,219,.4)";
                    break;
                case PlayerBarStyle.MinimalGlass:
                    topBg = "rgba(20,20,20,.32)"; botBg = "rgba(20,20,20,.22)"; blur = 8;
                    topBorder = "1px solid rgba(255,255,255,.08)"; botBorder = "1px solid rgba(255,255,255,.1)";
                    break;
                case PlayerBarStyle.GradientFade:
                    // No hard edge: gradients fade into the video. Blur is light on top of that.
                    topBg = "linear-gradient(to bottom,rgba(0,0,0,.75),rgba(0,0,0,0))";
                    botBg = "linear-gradient(to top,rgba(0,0,0,.75),rgba(0,0,0,0))";
                    blur = 6; topBorder = "0"; botBorder = "0";
                    break;
                default: // FrostedBlur — the official look
                    topBg = "rgba(10,10,10,.72)"; botBg = "rgba(18,18,18,.35)"; blur = 16;
                    topBorder = "1px solid rgba(255,255,255,.15)"; botBorder = "1px solid rgba(255,255,255,.25)";
                    break;
            }
        }

        // Builds the hosted player page. In every glass style the controls-hint legend is an
        // in-page overlay with backdrop-filter — Chromium blurs the live video on the GPU, no WPF.
        // GradientFade adds extra bottom padding on the top bar so the gradient has room to fade.
        private static string BuildPlayerHtml(string videoId, string title, PlayerBarStyle style, bool frostedBar)
        {
            var safeId = System.Text.RegularExpressions.Regex.Replace(videoId ?? string.Empty, "[^A-Za-z0-9_-]", "");

            GetBarSkin(style, out var topBg, out var botBg, out var blur, out var topBorder, out var botBorder);
            var blurCss = "backdrop-filter:blur(" + blur + "px) saturate(1.2);-webkit-backdrop-filter:blur(" + blur + "px) saturate(1.2);";
            // GradientFade reads better with taller bars so the fade has room.
            var topPad = style == PlayerBarStyle.GradientFade ? "18px 18px 30px" : "12px 18px";
            var botPad = style == PlayerBarStyle.GradientFade ? "30px 8px 14px" : "13px 8px";

            // Brand mark: the FullReel reel + play-triangle hub (TV/controller omitted), inline
            // SVG so it stays crisp at bar size. Violet→pink gradient matches the branding.
            const string brandMark =
                "<svg width='26' height='26' viewBox='0 0 120 120' style='vertical-align:-7px;margin-right:10px'>" +
                "<defs><linearGradient id='fvlg' x1='0' y1='0' x2='1' y2='1'>" +
                "<stop offset='0' stop-color='#8B5CF6'/><stop offset='1' stop-color='#EC4899'/></linearGradient></defs>" +
                "<circle cx='60' cy='60' r='46' fill='none' stroke='url(#fvlg)' stroke-width='11'/>" +
                "<circle cx='60' cy='29' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='89' cy='50' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='78' cy='85' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='42' cy='85' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='31' cy='50' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<path d='M 50 45 L 76 60 L 50 75 Z' fill='#F5F5F5'/></svg>";

            // Both bars are pointer-events:none — display-only; all input stays in C#. The top bar
            // auto-hides during playback and reappears on any input (fvShowTop() JS + C# poke).
            var topBar = !frostedBar ? "" :
                "<div id=\"tbar\" style=\"position:fixed;left:0;right:0;top:0;z-index:2147483647;" +
                "pointer-events:none;padding:" + topPad + ";box-sizing:border-box;" +
                "font:600 16px 'Segoe UI',sans-serif;color:#FFFFFF;" +
                "white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" +
                "background:" + topBg + ";border-bottom:" + topBorder + ";" + blurCss +
                "transition:transform .35s ease,opacity .35s ease;\">" +
                brandMark + HtmlEscape(title) +
                "</div>";

            // Legend + inline time on ONE row: controls centered, "current / total" subtly at the
            // far right. Grid keeps the legend truly centered regardless of the time width.
            const string legend =
                "<b style=\"color:#B39DDB\">A / Space</b> Play/Pause" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">◄ ►</b> Seek 10s" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">▲ ▼</b> Volume" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">Y / D</b> Download" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">Select / F</b> Fullscreen" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#B39DDB\">RB / P</b> Screenshot" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#EF9A9A\">B / Esc</b> Close";

            var bottomBar = !frostedBar ? "" :
                "<div id=\"bbar\" style=\"position:fixed;left:0;right:0;bottom:0;z-index:2147483647;" +
                "pointer-events:none;padding:" + botPad + ";box-sizing:border-box;" +
                "font:14px 'Segoe UI',sans-serif;color:#F5F5F5;" +
                "background:" + botBg + ";border-top:" + botBorder + ";" + blurCss +
                "transition:transform .35s ease,opacity .35s ease;" +
                "display:grid;grid-template-columns:1fr auto 1fr;align-items:center;column-gap:12px;\">" +
                "<span></span>" +
                "<span style=\"text-align:center\">" + legend + "</span>" +
                "<span style=\"text-align:right;font:600 12px 'Segoe UI',sans-serif;color:#BBB;padding-right:6px\">" +
                "<span id=\"cur\">0:00</span> / <span id=\"tot\">0:00</span></span>" +
                "</div>";

            return
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#p{width:100%;height:100%}</style>" +
                "</head><body><div id=\"p\"></div>" + topBar + bottomBar +
                "<script>" +
                "var player;" +
                "function onYouTubeIframeAPIReady(){" +
                "  player=new YT.Player('p',{videoId:'" + safeId + "'," +
                // vq=hd1080 requests a high-res stream (60fps lives at 1080p+); YouTube treats it
                // as a hint. onReady also suggests hd1080. Final quality is YouTube's call — the
                // embed biases toward 'auto', so 60fps only plays if the video has it + net allows.
                "    playerVars:{autoplay:1,controls:0,modestbranding:1,rel:0,playsinline:1,vq:'hd1080'}," +
                "    events:{" +
                "      onReady:function(){try{player.setPlaybackQuality('hd1080');}catch(e){}}," +
                // Once the video reaches PLAYING (state 1), reveal the top bar then auto-hide.
                "      onStateChange:function(e){if(e.data===1)fvShowTop();}}});" +
                "}" +
                "var s=document.createElement('script');s.src='https://www.youtube.com/iframe_api';" +
                "document.head.appendChild(s);" +
                // Bar visibility. The TOP bar always auto-hides after 4s (its established behavior).
                // The BOTTOM bar only auto-hides when fvBottomAuto is on — C# sets that ONLY while
                // the player is expanded to fullscreen, so the windowed player keeps its bar shown.
                // fvShow() reveals both + rearms the timers; C# pokes it on every input.
                "var _tt,_tb,fvBottomAuto=false;" +
                "function _set(id,show,dir){var e=document.getElementById(id);if(!e)return;" +
                "e.style.opacity=show?'1':'0';e.style.transform=show?'translateY(0)':('translateY('+dir+'100%)');}" +
                "window.fvSetBottomAuto=function(on){fvBottomAuto=!!on;" +
                "if(!fvBottomAuto){if(_tb)clearTimeout(_tb);_set('bbar',1,'');}else fvShow();};" +
                "window.fvShow=function(){_set('tbar',1,'-');_set('bbar',1,'');" +
                "if(_tt)clearTimeout(_tt);_tt=setTimeout(function(){_set('tbar',0,'-');},4000);" +
                "if(_tb)clearTimeout(_tb);" +
                "if(fvBottomAuto)_tb=setTimeout(function(){_set('bbar',0,'');},4000);};" +
                "window.fvShowTop=window.fvShow;" +
                // Progress ticker: update the current / total time labels ~2x/sec.
                "function _fmt(s){s=Math.max(0,Math.floor(s||0));var m=Math.floor(s/60);" +
                "var ss=s%60;return m+':'+(ss<10?'0':'')+ss;}" +
                "setInterval(function(){if(!window.player||!player.getDuration)return;" +
                "var d=player.getDuration()||0,c=player.getCurrentTime()||0;" +
                "var cu=document.getElementById('cur');if(cu)cu.textContent=_fmt(c);" +
                "var to=document.getElementById('tot');if(to&&d>0)to.textContent=_fmt(d);},500);" +
                // Capture keys at the document (capture phase) BEFORE the YouTube iframe sees them,
                // and forward to C# via postMessage. WPF PreviewKeyDown misses keys once focus is
                // inside the WebView2 HWND, so this is the reliable path — it also stops YouTube's
                // own keyboard controls from firing. Only our shortcut keys are intercepted.
                "var _keys={' ':1,'k':1,'K':1,'ArrowLeft':1,'ArrowRight':1,'ArrowUp':1,'ArrowDown':1," +
                "'d':1,'D':1,'f':1,'F':1,'p':1,'P':1,'Escape':1};" +
                "document.addEventListener('keydown',function(e){if(_keys[e.key]){e.preventDefault();" +
                "e.stopPropagation();try{chrome.webview.postMessage('key:'+e.key);}catch(x){}}},true);" +
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

        // Re-focus the web view whenever the window becomes active again (alt-tab back, etc.) —
        // deferred so it lands after WPF finishes its own activation focus handling.
        // Win32 SetFocus — the ONLY reliable way to restore keyboard to WebView2 after alt-tab.
        // WPF still thinks the control is focused, so Web.Focus() is a no-op; the SDK only tells
        // the browser it regained focus from its WM_SETFOCUS handler → MoveFocus(Programmatic).
        // Confirmed by-design per WebView2Feedback #4626; workaround per #185.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        // Deferred to ContextIdle — WPF sets its own focus AFTER Activated completes; earlier
        // priorities get overwritten (#185).
        private void FocusWebView(string reason)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (Web != null && Web.IsVisible && Web.Handle != IntPtr.Zero && Web.CoreWebView2 != null)
                    {
                        SetFocus(Web.Handle); // hits the SDK's WM_SETFOCUS → MoveFocus path
                        Logger.Info("[KbdDiag] SetFocus(Web.Handle) done (" + reason + ")");
                    }
                }
                catch (Exception ex) { Logger.Info("[KbdDiag] SetFocus threw: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
            Logger.Info("[KbdDiag] window Activated — restoring browser focus");
            FocusWebView("activated");
        }

        // On the hosting window's Closed — the single choke point for every close path.
        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (sender is Window w)
            {
                w.Closed -= OnWindowClosed;
                w.Activated -= OnWindowActivated;
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
                    Web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
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

        // Keyboard from inside the page ("key:<Key>"). Maps to the same PlayerAction dispatch as
        // the controller, so keys work regardless of where WebView2 focus is.
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = e.TryGetWebMessageAsString();
                Logger.Info("[KbdDiag] WebMessage: " + (msg ?? "null"));
                if (string.IsNullOrEmpty(msg) || !msg.StartsWith("key:")) return;
                var key = msg.Substring(4);
                PlayerAction action;
                switch (key)
                {
                    case " ":
                    case "k": case "K": action = PlayerAction.PlayPause; break;
                    case "ArrowRight": action = PlayerAction.SeekForward; break;
                    case "ArrowLeft":  action = PlayerAction.SeekBackward; break;
                    case "ArrowUp":    action = PlayerAction.VolumeUp; break;
                    case "ArrowDown":  action = PlayerAction.VolumeDown; break;
                    case "d": case "D": action = PlayerAction.Download; break;
                    case "f": case "F": action = PlayerAction.ToggleFullscreen; break;
                    case "p": case "P": action = PlayerAction.Screenshot; break;
                    case "Escape": action = PlayerAction.Close; break;
                    default: return;
                }
                DispatchKeyboardAction(action);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling web-message key in VideoPlayerDialog");
            }
        }

        // WPF-side keyboard (fires when focus is outside the web view — e.g. after alt-tab, or
        // re-raised by WebView2's WPF integration while the web view IS focused). Same mapping,
        // same deduped dispatch.
        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            Logger.Info("[KbdDiag] WPF PreviewKeyDown: " + e.Key);
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
                case System.Windows.Input.Key.F: action = PlayerAction.ToggleFullscreen; break;
                case System.Windows.Input.Key.P: action = PlayerAction.Screenshot; break;
                default: return;
            }
            e.Handled = true;
            DispatchKeyboardAction(action);
        }

        // Dedupes the two keyboard paths: one physical press can arrive from both the in-page
        // capture and WPF PreviewKeyDown within milliseconds. The SAME action inside the window
        // is treated as that duplicate and dropped; different actions pass through untouched.
        private PlayerAction _lastKeyAction = PlayerAction.None;
        private DateTime _lastKeyTime = DateTime.MinValue;
        // Wide enough to swallow the slow twin: the WebMessage copy of a key can arrive well
        // after the WPF re-raise under load (>120ms was observed — toggles like F cancelled
        // themselves while idempotent keys like Esc appeared to work). Costs: arrow-hold seek
        // repeats throttle to ~3/sec, which is fine for 10s jumps.
        private const int KeyDedupeMs = 350;

        private void DispatchKeyboardAction(PlayerAction action)
        {
            var now = DateTime.Now;
            if (action == _lastKeyAction && (now - _lastKeyTime).TotalMilliseconds < KeyDedupeMs)
                return;
            _lastKeyAction = action;
            _lastKeyTime = now;
            DispatchAction(action, keyboard: true);
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
                case PlayerAction.ToggleFullscreen:
                    ToggleFullscreen();
                    break;
                case PlayerAction.Screenshot:
                    _ = CaptureScreenshot();
                    break;
            }
        }

        // Save a PNG of the current video frame. Uses CoreWebView2.CapturePreviewAsync — it grabs
        // the actual rendered web content (the video), which a WPF window snapshot can't reach for
        // the WebView2 HwndHost surface. Lands in the plugin's Screenshots folder.
        private async System.Threading.Tasks.Task CaptureScreenshot()
        {
            try
            {
                if (Web?.CoreWebView2 == null) return;
                var dir = System.IO.Path.Combine(
                    _api.Paths.ConfigurationPath, "ExtraMetadata", "FullReel", "Screenshots");
                System.IO.Directory.CreateDirectory(dir);
                var safe = System.Text.RegularExpressions.Regex.Replace(_video?.Title ?? "video", "[^A-Za-z0-9 _-]", "").Trim();
                if (safe.Length > 60) safe = safe.Substring(0, 60);
                var file = System.IO.Path.Combine(dir, safe + "_" + Environment.TickCount + ".png");

                using (var fs = new System.IO.FileStream(file, System.IO.FileMode.Create))
                    await Web.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, fs);

                _api?.Notifications?.Add(new NotificationMessage(
                    "fullreel-shot", "Screenshot saved:\n" + file, NotificationType.Info));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Screenshot capture failed");
            }
        }

        // Fullscreen toggle state — tracked explicitly rather than read back from WindowState,
        // which is unreliable on a borderless window. Remembers the windowed bounds to restore.
        private bool _isFullscreen;
        private Rect _windowedBounds;

        // Expand the borderless player to fill the screen and back to windowed. Uses explicit
        // bounds (not WindowState.Maximized) — a borderless Maximized window can get stuck and
        // doesn't restore cleanly. Drops Topmost while fullscreen so screenshot tools can capture.
        private void ToggleFullscreen()
        {
            var w = Window.GetWindow(this);
            if (w == null) return;

            if (!_isFullscreen)
            {
                _windowedBounds = new Rect(w.Left, w.Top, w.Width, w.Height);
                var screen = System.Windows.SystemParameters.WorkArea; // full monitor minus taskbar
                w.Left = screen.Left; w.Top = screen.Top;
                w.Width = screen.Width; w.Height = screen.Height;
                _isFullscreen = true;
                Script("if(window.fvSetBottomAuto)fvSetBottomAuto(true);");  // bottom bar auto-hides in fullscreen
            }
            else
            {
                w.Left = _windowedBounds.Left; w.Top = _windowedBounds.Top;
                w.Width = _windowedBounds.Width; w.Height = _windowedBounds.Height;
                _isFullscreen = false;
                Script("if(window.fvSetBottomAuto)fvSetBottomAuto(false);"); // windowed: bottom bar stays
            }

            // Resizing can move focus out of the web page — pull it back so keys keep working.
            FocusWebView("fullscreen-toggle");
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
