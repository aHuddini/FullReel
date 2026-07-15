using System;
using System.Diagnostics;
using Playnite.SDK;

namespace FullVid.Services
{
    // Fires a playnite:// URI. Seam so the bridge is unit-testable without launching
    // a process. The Playnite SDK exposes only the inbound side (UriHandler.RegisterSource,
    // which UniPlaySong uses to RECEIVE these); there is no outbound API to invoke a URI,
    // so the default impl shells out — the OS routes playnite:// to the running Playnite
    // instance, which dispatches to UniPlaySong's registered "uniplaysong" source.
    public interface IUriInvoker
    {
        void Invoke(string uri);
    }

    // Launches a playnite:// URI via the OS protocol handler.
    public class ProcessUriInvoker : IUriInvoker
    {
        public void Invoke(string uri)
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
    }

    // Pauses/resumes UniPlaySong's game music while a video plays, via UniPlaySong's
    // external-control URI contract (playnite://uniplaysong/{pause|play}). Fail-soft:
    // if UniPlaySong isn't installed the URI just goes nowhere and we swallow it, so a
    // missing dependency never breaks playback.
    public class UniPlaySongBridge
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Exact strings verified against UniPlaySong's ExternalControlService contract.
        private const string PauseUri = "playnite://uniplaysong/pause";
        private const string PlayUri = "playnite://uniplaysong/play";

        private readonly IUriInvoker _invoker;

        public UniPlaySongBridge(IUriInvoker invoker)
        {
            _invoker = invoker;
        }

        public void Pause() => Fire(PauseUri);

        public void Resume() => Fire(PlayUri);

        private void Fire(string uri)
        {
            try
            {
                _invoker?.Invoke(uri);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "UniPlaySongBridge failed to fire " + uri + " (UniPlaySong not installed?)");
            }
        }
    }
}
