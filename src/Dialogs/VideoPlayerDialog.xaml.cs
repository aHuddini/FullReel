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
        ToggleBottomBar,   // RB / P — flip the bottom bar between shown and auto-hiding
        CycleQuality       // LB / Q — cycle the manual quality (auto → 720 → … → 2160)
    }

    // Fullscreen WebView2 YouTube player. Transport is driven entirely from C#
    // (ExecuteScriptAsync against the YT IFrame Player API) — the embedded page's own
    // controls are hidden. A = play/pause, B = close, D-pad = seek/volume, Y = download.
    // Pauses UniPlaySong's game music on open and resumes it on the window's Closed event
    // (fires on EVERY close path, so UPS is never left stuck paused).
    public partial class VideoPlayerDialog : UserControl, IControllerInputReceiver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // One WebView2 environment shared across every player open. CreateAsync spins up
        // browser-process coordination and is the heaviest part of opening the dialog; the same
        // env legally backs many WebView2 controls, so caching it makes the 2nd+ open reach first
        // frame noticeably faster. Built once on the UI thread (opens are UI-thread serialized, so
        // no lock is needed), reused thereafter.
        private static CoreWebView2Environment _sharedEnv;

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

        // Debug trace into FullReel.log (dead-input triage) — null when debug logging is off.
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
            if (button == ControllerInput.LeftShoulder) return PlayerAction.CycleQuality;

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

            // Default to un-swapped on any failure so A always confirms in the worst case.
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
            // Seed the session's bottom-bar preference; RB/P can flip it live.
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

            // If the browser grabs focus (mouse click on the video, WebView2 init auto-focus),
            // yank it straight back to the window — focus inside the WebView2 HWND makes Playnite
            // drop all controller events (see FocusHost). Nothing in the page needs focus: the
            // bars are pointer-events:none and all transport is driven from C#.
            Web.GotFocus += (s2, e2) => FocusHost("web-got-focus");

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
                // Env is cached across opens (see _sharedEnv) — only the first open pays for it.
                if (_sharedEnv == null)
                {
                    var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                    _sharedEnv = await CoreWebView2Environment.CreateAsync(null, null, options);
                }
                await Web.EnsureCoreWebView2Async(_sharedEnv);
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
                    PlayerPage.BuildEmbedScript(_settings?.ForceHdPlayback != false, _settings?.Prefer1080pFirst == true));
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

        // FrostedBlur = HTML overlay inside the page (BuildPlayerHtml includes it);
        // Performance = plain WPF strip below the video.
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

            var style = _settings?.PlayerBarStyle ?? PlayerBarStyle.MinimalGlass;
            var html = PlayerPage.BuildPlayerHtml(_video?.Id ?? string.Empty, _video?.Title ?? string.Empty,
                style, _frosted, _settings?.KeepBarOverBlack != false);
            var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
            var env = Web.CoreWebView2.Environment;
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
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
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // True when the window was Topmost and we dropped it on deactivation — restore on return.
        private bool _restoreTopmost;

        private void OnWindowActivated(object sender, EventArgs e)
        {
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

        // The single choke point for every close path.
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
                if (msg == "ready")
                {
                    _playerReady = true;
                    _dlog?.Debug($"[Player] YT ready (pending={_pendingAction?.ToString() ?? "none"})");
                    // The player is live now; make sure the web view actually has keyboard focus
                    // (initial focus can miss before the content is rendered), then replay any press
                    // the user made during the init gap so it isn't silently dropped.
                    FocusHost("player-ready");
                    // Windowed opens with the bottom bar pinned shown — auto-hide is a
                    // fullscreen-only behavior. Push directly (not via Script, which no-ops until
                    // _webReady) so the state is deterministic and the bar can't drift to
                    // auto-hiding on its own in windowed.
                    var autoHide = _isFullscreen && _bottomAuto;
                    Web?.CoreWebView2?.ExecuteScriptAsync(
                        "if(window.fvSetBottomAuto)fvSetBottomAuto(" + (autoHide ? "true" : "false") + ");");
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
                    case "q": case "Q": action = PlayerAction.CycleQuality; break;
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
                case System.Windows.Input.Key.Q: action = PlayerAction.CycleQuality; break;
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
                    CloseDeferred();
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
                case PlayerAction.CycleQuality:
                    Script("if(window.fvCycleQuality)fvCycleQuality();");
                    break;
            }
        }

        // Guards against a second close press queuing another deferred close.
        private bool _closing;

        // Close, but hold the window (and its keyboard focus) open for the router's post-close
        // suppression window first. A controller B is notify-only — Playnite ALSO sees it and,
        // the instant the modal closes, its Fullscreen grid reclaims focus and the trailing
        // controller events (repeat/release bounce) activate the focused game tile → the game
        // launches or the theme view animates. Keeping the modal focused ~100ms past the press
        // means those trailing events land on this (still-focused) window, not the grid. Wired to
        // a one-shot DispatcherTimer so we stay on the UI thread. Keyboard Esc is already consumed
        // by WPF (never leaks) — it pays the same small delay, which is imperceptible.
        private void CloseDeferred()
        {
            if (_closing) return;
            _closing = true;

            var window = Window.GetWindow(this);
            if (window == null) return;

            FocusHost("close-deferred");

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Services.Controller.ControllerEventRouter.PostCloseSuppressMs)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                window.Close();
            };
            timer.Start();
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
