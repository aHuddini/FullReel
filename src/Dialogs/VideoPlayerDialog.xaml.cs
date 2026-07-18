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
        ToggleBottomBar    // RB / P — flip the bottom bar between shown and auto-hiding
    }

    // Fullscreen WebView2 YouTube player. Transport is driven entirely from C#
    // (ExecuteScriptAsync against the YT IFrame Player API) — the embedded page's own
    // controls are hidden. A = play/pause, B = close, D-pad = seek/volume, Y = download.
    // Pauses UniPlaySong's game music on open and resumes it on the window's Closed event
    // (fires on EVERY close path, so UPS is never left stuck paused).
    public partial class VideoPlayerDialog : UserControl, IControllerInputReceiver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IPlayniteAPI _api;
        private readonly FullVidSettings _settings;
        private readonly UniPlaySongBridge _bridge;
        private readonly VideoResult _video;
        private readonly Action<VideoResult> _onDownload;
        private readonly bool _swapAB;

        // Guard so we only Resume() UPS if we actually Paused() it (setting was on).
        private bool _upsPaused;
        private bool _webReady;

        // The YT IFrame player object is created asynchronously (onYouTubeIframeAPIReady). Until
        // 'ready' arrives, transport scripts (if(window.player){...}) no-op, so a press in that
        // window would vanish and the player feels dead until the user presses again. We hold the
        // first such press and replay it once 'ready' fires. See OnWebMessageReceived("ready").
        private bool _playerReady;
        private PlayerAction? _pendingAction;

        // Debug trace into FullVid.log (dead-input triage) — null when debug logging is off.
        private Common.FileLogger _dlog;

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
            if (button == ControllerInput.RightShoulder) return PlayerAction.ToggleBottomBar;

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
            _dlog = (Application.Current?.Properties?[DialogHelper.PluginPropertyKey] as FullVid)?.GetFileLogger();
            _dlog?.Debug("[Player] Loaded, controller registered");
            // Seed the session's bottom-bar preference from settings (RB/P can flip it live).
            _bottomAuto = _settings?.FullscreenBarAutoHide ?? false;

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
            // If the browser grabs focus (mouse click on the video, WebView2 init auto-focus),
            // yank it straight back to the window — focus inside the WebView2 HWND makes Playnite
            // drop all controller events (see FocusHost). Nothing in the page needs focus: the
            // bars are pointer-events:none and all transport is driven from C#.
            Web.GotFocus += (s2, e2) =>
            {
                Logger.Info("[KbdDiag] WebView2 GotFocus — yanking focus back to host");
                FocusHost("web-got-focus");
            };
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
                // Topmost (Fullscreen sessions) would keep painting over alt-tabbed windows —
                // drop it while we're not the active window, restore it when we are again.
                window.Deactivated += OnWindowDeactivated;
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

            // Embed-side script: reports the playing resolution to the bar label, and (when the
            // setting is on) asks for HD via YouTube's internal player API — the official quality
            // APIs (setPlaybackQuality / vq / suggestedQuality) have been documented no-ops since
            // 2019, and the embed's automatic pick trends low. Injected into every child document
            // (the script gates itself to YouTube embed frames); registered BEFORE Navigate so
            // the embed iframe gets it. Fully fail-soft: injection failure, missing internals, or
            // a starved connection all end in normal adaptive playback (see BuildEmbedScript).
            try
            {
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    BuildEmbedScript(_settings?.ForceHdPlayback != false));
            }
            catch (Exception ex)
            {
                _dlog?.Debug("[Player] embed script injection failed (auto quality, no label): " + ex.Message);
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
            // Keep keyboard focus on the WPF window from the start (see FocusHost — focus inside
            // the WebView2 HWND kills Playnite's controller event delivery).
            FocusHost("initial");
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
                "<b style=\"color:#B39DDB\">RB / P</b> Bottom bar" +
                "<span style=\"color:rgba(255,255,255,.4)\">&nbsp;&nbsp;•&nbsp;&nbsp;</span>" +
                "<b style=\"color:#EF9A9A\">B / Esc</b> Close";

            var bottomBar = !frostedBar ? "" :
                "<div id=\"bbar\" style=\"position:fixed;left:0;right:0;bottom:0;z-index:2147483647;" +
                "pointer-events:none;padding:" + botPad + ";box-sizing:border-box;" +
                "font:14px 'Segoe UI',sans-serif;color:#F5F5F5;" +
                "background:" + botBg + ";border-top:" + botBorder + ";" + blurCss +
                "transition:transform .35s ease,opacity .35s ease;" +
                "display:grid;grid-template-columns:1fr auto 1fr;align-items:center;column-gap:12px;\">" +
                // Left cell: live playing-resolution pill (fed by the embed script's postMessage).
                // Hidden while empty via the #qual:empty rule; violet tint matches the key hints.
                // The pill opts back into pointer events (the bar is pointer-events:none) — a
                // click cycles quality manually: auto -> 720p -> 1080p -> 1440p -> 2160p.
                "<span style=\"text-align:left;padding-left:6px\">" +
                "<span id=\"qual\" title=\"Click: change quality\" style=\"display:inline-block;" +
                "pointer-events:auto;cursor:pointer;font:600 11px 'Segoe UI',sans-serif;" +
                "color:#E6DFF7;background:rgba(139,92,246,.28);border:1px solid rgba(179,157,219,.4);" +
                "border-radius:999px;padding:2px 10px;letter-spacing:.3px\"></span></span>" +
                "<span style=\"text-align:center\">" + legend + "</span>" +
                "<span style=\"text-align:right;font:600 12px 'Segoe UI',sans-serif;color:#BBB;padding-right:6px\">" +
                "<span id=\"cur\">0:00</span> / <span id=\"tot\">0:00</span></span>" +
                "</div>";

            return
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                // #p (the YT iframe) is a centered 16:9 COVER block, not width/height:100%. A
                // 100% iframe letterboxes the video whenever the window aspect isn't exactly
                // 16:9 — in fullscreen the black strip lands at the bottom, right under the
                // glass bar, so its backdrop-filter blurs black and looks broken. Cover sizing
                // keeps real video under every edge; the sliver of overflow is cropped.
                "<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}" +
                "#p{position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);" +
                "width:max(100vw,177.7778vh);height:max(100vh,56.25vw)}" +
                "#qual:empty{display:none !important}</style>" +
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
                // Tell C# the YT player object is live. Until this fires, transport scripts
                // (if(window.player){...}) silently no-op, so early controller/key presses vanish —
                // C# holds the first press and replays it on 'ready'.
                "      onReady:function(){try{player.setPlaybackQuality('hd1080');}catch(e){}" +
                "        try{chrome.webview.postMessage('ready');}catch(e){}}," +
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
                // Quality label: the embed-side script (BuildEmbedScript) posts {fvq:'1080p'} from
                // inside the iframe on every adaptive resolution switch; only YouTube frames are
                // accepted and only the expected string shape is rendered.
                "window.addEventListener('message',function(e){try{" +
                "if(!/(^|\\.)youtube(-nocookie)?\\.com$/.test(new URL(e.origin).hostname))return;" +
                "var d=e.data;if(!d||typeof d.fvq!=='string'||!/^\\d{3,4}p$/.test(d.fvq))return;" +
                "var q=document.getElementById('qual');if(q)q.textContent=d.fvq;}catch(x){}});" +
                // Click the pill to cycle quality manually. The request goes into the embed
                // iframe (#p — YT.Player turns the div into the iframe), which clamps it to
                // what's available; the label snaps back to the REAL decoded resolution on the
                // next report, so a declined pick is visible immediately.
                "var _qmodes=['auto','hd720','hd1080','hd1440','hd2160'];" +
                "var _qlabels={auto:'auto',hd720:'720p',hd1080:'1080p',hd1440:'1440p',hd2160:'2160p'};" +
                "var _qi=0;" +
                "document.addEventListener('click',function(e){try{" +
                "if(!e.target||e.target.id!=='qual')return;" +
                "_qi=(_qi+1)%_qmodes.length;var m=_qmodes[_qi];" +
                "var f=document.getElementById('p');" +
                "if(f&&f.contentWindow)f.contentWindow.postMessage({fvSet:m},'*');" +
                "var ql=document.getElementById('qual');if(ql)ql.textContent=_qlabels[m];" +
                "if(window.fvShow)fvShow();" +
                "}catch(x){}});" +
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

        // Script injected into every child document; runs only inside YouTube embed frames.
        // Two jobs: (1) report the ACTUAL playing resolution (video.videoHeight — standard API,
        // updates via the 'resize' event on adaptive switches) to the parent page, which shows
        // it in the bottom bar; (2) when forceHd, request HD via the INTERNAL #movie_player
        // element API (setPlaybackQualityRange) — the approach maintained quality extensions
        // ship today — because the official IFrame quality APIs are no-ops. Defensive by
        // design, playback always wins over quality:
        //   • everything try/caught, every internal call existence-checked → worst case = auto
        //   • bounded polling (~20s max), no infinite loops, never touches playback state
        //   • stall watchdog: 3 genuine buffering stalls (seek-triggered ones filtered) release
        //     the quality lock back to the full adaptive range so the video keeps playing
        private static string BuildEmbedScript(bool forceHd)
        {
            return
                "(function(){try{" +
                // Only YouTube embed frames — every other document exits immediately.
                "if(!/(^|\\.)youtube(-nocookie)?\\.com$/.test(location.hostname))return;" +
                "if(location.pathname.indexOf('/embed')!==0)return;" +
                "var FORCE=" + (forceHd ? "true" : "false") + ";" +
                "var tries=0,stalls=0,released=false,lastSeek=0;" +
                // Prefer HD matched to the screen when possible, else fall back toward 1080p:
                // walk the ladder up from 720 remembering the largest AVAILABLE rung, stop at the
                // first one that covers the physical screen (height x DPR). A 4K monitor gets
                // hd2160 when the video has it; a video maxing out at 1080 gets hd1080. No
                // availability data -> the walk stops at the screen-need rung blind. null = auto.
                "function pick(p){try{" +
                "var need=(screen.height||1080)*(window.devicePixelRatio||1);" +
                "var ladder=['hd720','hd1080','hd1440','hd2160'];" +
                "var hs={hd720:720,hd1080:1080,hd1440:1440,hd2160:2160};" +
                "var av=null;" +
                "if(typeof p.getAvailableQualityData==='function'){var d=p.getAvailableQualityData();" +
                "if(d&&d.length){av={};for(var i=0;i<d.length;i++)av[d[i].quality]=1;}}" +
                "var chosen=null;" +
                "for(var j=0;j<ladder.length;j++){var q=ladder[j];" +
                "if(av&&!av[q])continue;" +
                "chosen=q;if(hs[q]>=need)break;}" +
                "return chosen;}catch(e){return null;}}" +
                "function apply(){try{if(!FORCE||released)return;" +
                "var p=document.getElementById('movie_player');" +
                "if(!p||typeof p.setPlaybackQualityRange!=='function')return;" +
                "var q=pick(p);if(q)p.setPlaybackQualityRange(q,q);}catch(e){}}" +
                // Release the lock: restore the full adaptive range so ABR is free again.
                "function release(){released=true;try{" +
                "var p=document.getElementById('movie_player');" +
                "if(p&&typeof p.setPlaybackQualityRange==='function')" +
                "p.setPlaybackQualityRange('tiny','highres');}catch(e){}}" +
                "function arm(){try{" +
                "var v=document.querySelector('video');" +
                "var p=document.getElementById('movie_player');" +
                "if(!v||!p){if(++tries<40)setTimeout(arm,500);return;}" +
                // Report the ACTUAL decoded resolution to the parent page's bar label.
                // videoHeight is 0 until metadata loads; 'resize' fires on every adaptive switch.
                "function report(){try{var h=v.videoHeight||0;if(!h)return;" +
                "window.parent.postMessage({fvq:h+'p'},'*');}catch(e){}}" +
                "v.addEventListener('resize',report);" +
                "v.addEventListener('loadedmetadata',report);" +
                "report();setTimeout(report,1500);" +
                "v.addEventListener('seeking',function(){lastSeek=Date.now();});" +
                // 'waiting' = buffering stall — but seeks fire it too, so ignore those.
                "v.addEventListener('waiting',function(){if(released)return;" +
                "if(Date.now()-lastSeek<2000)return;" +
                "if(++stalls>=3)release();});" +
                "v.addEventListener('canplay',apply);" +
                "apply();setTimeout(apply,2000);" +
                "}catch(e){}}" +
                // Manual quality picks from the parent page's pill (click-to-cycle). 'auto'
                // restores the full adaptive range and stops our forcing; a specific rung is
                // clamped to the nearest available at-or-below the request. Re-arms the stall
                // watchdog so a too-ambitious manual pick still degrades gracefully.
                "window.addEventListener('message',function(e){try{" +
                "if(e.origin!=='https://fullvid.player')return;" +
                "var d=e.data;if(!d||typeof d.fvSet!=='string')return;" +
                "var p=document.getElementById('movie_player');" +
                "if(!p||typeof p.setPlaybackQualityRange!=='function')return;" +
                "stalls=0;" +
                "if(d.fvSet==='auto'){released=true;p.setPlaybackQualityRange('tiny','highres');return;}" +
                "var order=['hd2160','hd1440','hd1080','hd720'];" +
                "var i=order.indexOf(d.fvSet);if(i<0)return;" +
                "var av=null;" +
                "if(typeof p.getAvailableQualityData==='function'){var dd=p.getAvailableQualityData();" +
                "if(dd&&dd.length){av={};for(var k=0;k<dd.length;k++)av[dd[k].quality]=1;}}" +
                "var q=null;for(;i<order.length;i++){if(!av||av[order[i]]){q=order[i];break;}}" +
                "if(!q)return;released=false;p.setPlaybackQualityRange(q,q);" +
                "}catch(x){}});" +
                "if(document.readyState!=='loading')arm();" +
                "else document.addEventListener('DOMContentLoaded',arm);" +
                "}catch(e){}})();";
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
        // Keyboard focus must live on the WPF window, NEVER inside the WebView2 HWND. The
        // browser's child HWND belongs to msedgewebview2.exe, so focusing it nulls WPF's
        // PrimaryKeyboardDevice.ActiveSource — and Playnite's GameController.SendControllerInput
        // early-returns on that null BEFORE invoking the plugin ButtonChanged event, killing ALL
        // controller input for the dialog (only a D-pad press revives it, via its simulated
        // arrow key re-latching WPF's keyboard device). With focus held on the window instead:
        // controller events keep flowing, and every shortcut arrives via WPF PreviewKeyDown.
        // Deferred to ContextIdle so we land AFTER WPF/WebView2 finish their own focus moves.
        private void FocusHost(string reason)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var w = Window.GetWindow(this);
                    if (w != null)
                    {
                        w.Focus();
                        System.Windows.Input.Keyboard.Focus(w);
                        Logger.Info("[KbdDiag] FocusHost done (" + reason + ")");
                    }
                }
                catch (Exception ex) { Logger.Info("[KbdDiag] FocusHost threw: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // True when the window was Topmost and we dropped it on deactivation — restore on return.
        private bool _restoreTopmost;

        private void OnWindowActivated(object sender, EventArgs e)
        {
            Logger.Info("[KbdDiag] window Activated — restoring host focus");
            if (_restoreTopmost && sender is Window aw)
            {
                aw.Topmost = true;
                _restoreTopmost = false;
            }
            FocusHost("activated");
        }

        // Alt-tabbing away from a Topmost fullscreen player left it painted over every other
        // window. Drop Topmost while deactivated so other apps are usable; restored on return.
        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            if (sender is Window w && w.Topmost)
            {
                w.Topmost = false;
                _restoreTopmost = true;
            }
        }

        // On the hosting window's Closed — the single choke point for every close path.
        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (sender is Window w)
            {
                w.Closed -= OnWindowClosed;
                w.Activated -= OnWindowActivated;
                w.Deactivated -= OnWindowDeactivated;
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
                if (msg == "ready")
                {
                    _playerReady = true;
                    _dlog?.Debug($"[Player] YT ready (pending={_pendingAction?.ToString() ?? "none"})");
                    // The player is live now; make sure the web view actually has keyboard focus
                    // (initial focus can miss before the content is rendered), then replay any press
                    // the user made during the init gap so it isn't silently dropped.
                    FocusHost("player-ready");
                    if (_pendingAction.HasValue)
                    {
                        var pending = _pendingAction.Value;
                        _pendingAction = null;
                        DispatchAction(pending);
                    }
                    return;
                }
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
                    case "p": case "P": action = PlayerAction.ToggleBottomBar; break;
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
                case System.Windows.Input.Key.P: action = PlayerAction.ToggleBottomBar; break;
                default: return;
            }
            e.Handled = true;
            DispatchKeyboardAction(action);
        }

        // Dedupes ALL input paths — one physical press can arrive up to THREE ways within
        // milliseconds: the SDK controller event, the in-page key capture, and WPF
        // PreviewKeyDown (Playnite Fullscreen synthesizes arrow keys from D-pad, so a D-pad
        // press produces a controller event AND a key event). The SAME action inside the
        // window is treated as that duplicate and dropped; different actions pass untouched.
        // Gating only pairs of paths (the pre-fix design) let the third path double-fire.
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
            {
                _dlog?.Debug($"[Player] {action} deduped (twin within {KeyDedupeMs}ms)");
                return;
            }
            _lastKeyAction = action;
            _lastKeyTime = now;
            _dlog?.Debug($"[Player] {action} dispatch (playerReady={_playerReady} webReady={_webReady})");
            DispatchAction(action);
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
                // Through the SAME dedupe as the key paths — Playnite Fullscreen synthesizes
                // arrow keys from D-pad, so this press's key twin lands there within ms.
                DispatchKeyboardAction(Decide(button, _swapAB));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling controller input in VideoPlayerDialog");
            }
        }

        public void OnControllerButtonReleased(ControllerInput button) { }

        private void DispatchAction(PlayerAction action)
        {
            // Player-dependent actions (transport) are no-ops until the YT player object exists.
            // Hold the most recent one and replay it when 'ready' arrives, so the first press
            // during the ~1s init gap isn't silently lost. Window-level actions (Close, Fullscreen,
            // ToggleBottomBar, Download) don't touch window.player, so they always run immediately.
            if (!_playerReady && ActionNeedsPlayer(action))
            {
                _pendingAction = action;
                return;
            }

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
                    Script("if(window.player){player.seekTo(player.getCurrentTime()+10,true);}");
                    break;
                case PlayerAction.SeekBackward:
                    Script("if(window.player){player.seekTo(Math.max(0,player.getCurrentTime()-10),true);}");
                    break;
                case PlayerAction.VolumeUp:
                    Script("if(window.player){player.setVolume(Math.min(100,player.getVolume()+10));}");
                    break;
                case PlayerAction.VolumeDown:
                    Script("if(window.player){player.setVolume(Math.max(0,player.getVolume()-10));}");
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
                case PlayerAction.ToggleBottomBar:
                    // Flip the bottom bar between always-shown and auto-hiding. Only meaningful
                    // in fullscreen — windowed keeps the bar shown, and _bottomAuto tracks the
                    // fullscreen state so restoring windowed doesn't strand a hidden bar.
                    _bottomAuto = !_bottomAuto;
                    Script("if(window.fvSetBottomAuto)fvSetBottomAuto(" + (_bottomAuto ? "true" : "false") + ");");
                    break;
            }
        }

        // Fullscreen toggle state — tracked explicitly rather than read back from WindowState,
        // which is unreliable on a borderless window. Remembers the windowed bounds to restore.
        private bool _isFullscreen;
        private Rect _windowedBounds;

        // Sticky bottom-bar preference, mirrors the JS fvBottomAuto flag: true = auto-hide,
        // false = always shown. Seeded from Settings.FullscreenBarAutoHide in OnDialogLoaded;
        // RB/P flips it live and the choice survives fullscreen enter/exit for the session —
        // those just re-apply the current preference to the page, they don't reset it.
        private bool _bottomAuto;

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
                if (_api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen)
                {
                    // Fullscreen theme: no taskbar on screen — take the entire monitor. WorkArea
                    // would leave a dead strip where the (hidden) taskbar lives.
                    w.Left = 0; w.Top = 0;
                    w.Width = System.Windows.SystemParameters.PrimaryScreenWidth;
                    w.Height = System.Windows.SystemParameters.PrimaryScreenHeight;
                }
                else
                {
                    // Desktop: stop at the work area so the taskbar stays reachable.
                    var screen = System.Windows.SystemParameters.WorkArea;
                    w.Left = screen.Left; w.Top = screen.Top;
                    w.Width = screen.Width; w.Height = screen.Height;
                }
                _isFullscreen = true;
                // Re-apply the current bar preference (don't reset it) so an RB/P choice made
                // before entering fullscreen is honored here.
                Script("if(window.fvSetBottomAuto)fvSetBottomAuto(" + (_bottomAuto ? "true" : "false") + ");");
            }
            else
            {
                w.Left = _windowedBounds.Left; w.Top = _windowedBounds.Top;
                w.Width = _windowedBounds.Width; w.Height = _windowedBounds.Height;
                _isFullscreen = false;
                // Windowed always shows the bar regardless of preference (no auto-hide when the
                // player isn't filling the screen); the preference itself is left untouched.
                Script("if(window.fvSetBottomAuto)fvSetBottomAuto(false);");
            }

            // Resizing can shuffle focus — make sure it stays on the host window.
            FocusHost("fullscreen-toggle");
        }

        // Fire a transport script against the YT IFrame API. No-op until the CoreWebView2
        // navigation has completed.
        private void Script(string js)
        {
            if (!_webReady || Web?.CoreWebView2 == null) return;
            _ = Web.CoreWebView2.ExecuteScriptAsync(js);
        }

        // Transport actions call into window.player; window-chrome actions don't. Only the former
        // must wait for the player-ready signal (see DispatchAction's hold-and-replay).
        private static bool ActionNeedsPlayer(PlayerAction action)
        {
            switch (action)
            {
                case PlayerAction.PlayPause:
                case PlayerAction.SeekForward:
                case PlayerAction.SeekBackward:
                case PlayerAction.VolumeUp:
                case PlayerAction.VolumeDown:
                    return true;
                default:
                    return false;
            }
        }

    }
}
