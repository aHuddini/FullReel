using FullVid.Dialogs;
using Playnite.SDK.Events;
using NUnit.Framework;

namespace FullVid.Tests
{
    // Pure decision-table tests for VideoResultsDialog.Decide — the controller-button
    // mapping seam. No UI, no dispatcher. swapAB models Playnite's
    // SwapConfirmCancelButtons: when true, A and B trade their confirm/cancel roles.
    [TestFixture]
    public class VideoResultsReceiverTests
    {
        // --- Default mapping (swapAB = false) ---

        [Test]
        public void A_OnItem_ReturnsWatch() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.A, 0, 3, false), Is.EqualTo(DialogAction.Watch));

        [Test]
        public void Y_OnItem_ReturnsDownload() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.Y, 0, 3, false), Is.EqualTo(DialogAction.Download));

        [Test]
        public void B_ReturnsClose() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.B, 0, 3, false), Is.EqualTo(DialogAction.Close));

        [Test]
        public void DPadDown_ReturnsNavigateDown() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.DPadDown, 0, 3, false), Is.EqualTo(DialogAction.NavigateDown));

        [Test]
        public void DPadUp_ReturnsNavigateUp() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.DPadUp, 1, 3, false), Is.EqualTo(DialogAction.NavigateUp));

        [Test]
        public void UnmappedButton_ReturnsNone() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.Start, 0, 3, false), Is.EqualTo(DialogAction.None));

        // --- Empty list: A/Y have no item to act on ---

        [Test]
        public void A_OnEmptyList_ReturnsNone() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.A, 0, 0, false), Is.EqualTo(DialogAction.None));

        [Test]
        public void Y_OnEmptyList_ReturnsNone() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.Y, 0, 0, false), Is.EqualTo(DialogAction.None));

        [Test]
        public void B_OnEmptyList_StillCloses() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.B, 0, 0, false), Is.EqualTo(DialogAction.Close));

        // --- Swapped mapping (swapAB = true): A/B trade confirm/cancel roles ---

        [Test]
        public void Swapped_B_OnItem_ReturnsWatch() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.B, 0, 3, true), Is.EqualTo(DialogAction.Watch));

        [Test]
        public void Swapped_A_ReturnsClose() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.A, 0, 3, true), Is.EqualTo(DialogAction.Close));

        [Test]
        public void Swapped_Y_StillReturnsDownload() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.Y, 0, 3, true), Is.EqualTo(DialogAction.Download));

        [Test]
        public void Swapped_DPadDown_StillNavigates() =>
            Assert.That(VideoResultsDialog.Decide(ControllerInput.DPadDown, 0, 3, true), Is.EqualTo(DialogAction.NavigateDown));
    }
}
