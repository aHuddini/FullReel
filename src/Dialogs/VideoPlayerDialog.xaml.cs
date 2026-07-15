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

            // Resume UPS on EVERY close path (B, window X, error, playback-ended) — wired to
            // the hosting window's Closed, not the B handler, so UPS is never left paused.
            var window = Window.GetWindow(this);
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
                await Web.EnsureCoreWebView2Async(null);
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

            // controls=0 hides YouTube's own UI; enablejsapi=1 exposes the IFrame Player API
            // so all transport is driven from C#.
            var url = "https://www.youtube.com/embed/" + Uri.EscapeDataString(_video?.Id ?? string.Empty) +
                      "?autoplay=1&controls=0&modestbranding=1&enablejsapi=1";
            Web.CoreWebView2.Navigate(url);
        }

        private void OnDialogUnloaded(object sender, RoutedEventArgs e)
        {
            GetRouter()?.Unregister(this);
        }

        // On the hosting window's Closed — the single choke point for every close path.
        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (sender is Window w)
                w.Closed -= OnWindowClosed;

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
                    Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                Web?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error disposing WebView2");
            }
        }

        // Bootstrap the YT IFrame Player API so `player.playVideo()` etc. work. The embed
        // page already loaded www-widgetapi; we bind the existing iframe to a YT.Player once
        // the API is ready. Fire-and-forget; transport calls no-op until `player` exists.
        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webReady = e.IsSuccess;
            if (!e.IsSuccess) return;

            const string bootstrap =
                "(function(){" +
                "  window.__fvBind=function(){" +
                "    if(window.YT&&YT.Player){window.player=new YT.Player(document.getElementsByTagName('iframe')[0]||document.body);}" +
                "  };" +
                "  if(window.YT&&window.YT.Player){window.__fvBind();}" +
                "  else{window.onYouTubeIframeAPIReady=window.__fvBind;" +
                "    if(!document.getElementById('__fvapi')){var s=document.createElement('script');s.id='__fvapi';s.src='https://www.youtube.com/iframe_api';document.head.appendChild(s);}}" +
                "})();";

            _ = Web.CoreWebView2.ExecuteScriptAsync(bootstrap);
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
                switch (Decide(button, _swapAB))
                {
                    case PlayerAction.PlayPause:
                        // Toggle via the player's own state so one A press flips play<->pause.
                        Script("if(window.player){var s=player.getPlayerState&&player.getPlayerState();" +
                               "if(s===1){player.pauseVideo();}else{player.playVideo();}}");
                        break;
                    case PlayerAction.SeekForward:
                        if (TryDpad()) Script("if(window.player){player.seekTo(player.getCurrentTime()+10,true);}");
                        break;
                    case PlayerAction.SeekBackward:
                        if (TryDpad()) Script("if(window.player){player.seekTo(Math.max(0,player.getCurrentTime()-10),true);}");
                        break;
                    case PlayerAction.VolumeUp:
                        if (TryDpad()) Script("if(window.player){player.setVolume(Math.min(100,player.getVolume()+10));}");
                        break;
                    case PlayerAction.VolumeDown:
                        if (TryDpad()) Script("if(window.player){player.setVolume(Math.max(0,player.getVolume()-10));}");
                        break;
                    case PlayerAction.Download:
                        // Close the player first, then hand off to the caller's download flow.
                        _onDownload?.Invoke(_video);
                        break;
                    case PlayerAction.Close:
                        Window.GetWindow(this)?.Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling controller input in VideoPlayerDialog");
            }
        }

        public void OnControllerButtonReleased(ControllerInput button) { }

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
