using FullVid.Dialogs;
using FullVid.Services;
using Playnite.SDK.Events;
using NUnit.Framework;

namespace FullVid.Tests
{
    // Pure decision-table tests for VideoPlayerDialog.Decide (controller-button mapping)
    // plus the UniPlaySongBridge URI contract. No UI, no dispatcher, no WebView2.
    // swapAB models Playnite's SwapConfirmCancelButtons: when true, A and B trade their
    // PlayPause(confirm)/Close(cancel) roles; Y and the D-pad are unaffected.
    [TestFixture]
    public class PlayerReceiverTests
    {
        // --- Default mapping (swapAB = false) ---

        [Test]
        public void A_ReturnsPlayPause() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.A, false), Is.EqualTo(PlayerAction.PlayPause));

        [Test]
        public void B_ReturnsClose() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.B, false), Is.EqualTo(PlayerAction.Close));

        [Test]
        public void DPadRight_ReturnsSeekForward() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.DPadRight, false), Is.EqualTo(PlayerAction.SeekForward));

        [Test]
        public void DPadLeft_ReturnsSeekBackward() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.DPadLeft, false), Is.EqualTo(PlayerAction.SeekBackward));

        [Test]
        public void DPadUp_ReturnsVolumeUp() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.DPadUp, false), Is.EqualTo(PlayerAction.VolumeUp));

        [Test]
        public void DPadDown_ReturnsVolumeDown() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.DPadDown, false), Is.EqualTo(PlayerAction.VolumeDown));

        [Test]
        public void Y_ReturnsDownload() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.Y, false), Is.EqualTo(PlayerAction.Download));

        [Test]
        public void UnmappedButton_ReturnsNone() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.Start, false), Is.EqualTo(PlayerAction.None));

        // --- Swapped mapping (swapAB = true): A/B trade PlayPause/Close ---

        [Test]
        public void Swapped_B_ReturnsPlayPause() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.B, true), Is.EqualTo(PlayerAction.PlayPause));

        [Test]
        public void Swapped_A_ReturnsClose() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.A, true), Is.EqualTo(PlayerAction.Close));

        [Test]
        public void Swapped_Y_StillReturnsDownload() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.Y, true), Is.EqualTo(PlayerAction.Download));

        [Test]
        public void Swapped_DPadRight_StillSeeksForward() =>
            Assert.That(VideoPlayerDialog.Decide(ControllerInput.DPadRight, true), Is.EqualTo(PlayerAction.SeekForward));

        // --- UniPlaySongBridge URI contract ---

        private class FakeUriInvoker : IUriInvoker
        {
            public string LastUri;
            public void Invoke(string uri) => LastUri = uri;
        }

        [Test]
        public void Pause_FiresUniPlaySongPauseUri()
        {
            var fake = new FakeUriInvoker();
            new UniPlaySongBridge(fake).Pause();
            Assert.That(fake.LastUri, Is.EqualTo("playnite://uniplaysong/pause"));
        }

        [Test]
        public void Resume_FiresPlayUri()
        {
            var fake = new FakeUriInvoker();
            new UniPlaySongBridge(fake).Resume();
            Assert.That(fake.LastUri, Is.EqualTo("playnite://uniplaysong/play"));
        }
    }
}
