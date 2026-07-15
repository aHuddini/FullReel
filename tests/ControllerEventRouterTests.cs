using System.Collections.Generic;
using FullVid.Services.Controller;
using Playnite.SDK.Events;
using NUnit.Framework;

namespace FullVid.Tests
{
    // Stack behaviour of ControllerEventRouter — the shared seam both dialogs push onto.
    // HandleButtonPressed dispatches via Application.Current.Dispatcher (null in a headless
    // run), so these assert on the routing decision — which receiver Peek() would hand the
    // press to — rather than the async invoke. Ctor FileLogger is optional (pass none).
    [TestFixture]
    public class ControllerEventRouterTests
    {
        // Hand-rolled fake: records every button it's told it received.
        private class FakeReceiver : IControllerInputReceiver
        {
            public readonly List<ControllerInput> Pressed = new List<ControllerInput>();
            public void OnControllerButtonPressed(ControllerInput button) => Pressed.Add(button);
            public void OnControllerButtonReleased(ControllerInput button) { }
        }

        [Test]
        public void Register_MakesReceiverActive()
        {
            var router = new ControllerEventRouter();
            var a = new FakeReceiver();

            router.Register(a);

            Assert.That(router.HasActiveReceiver, Is.True);
        }

        [Test]
        public void TopOfStackWins_LastRegisteredReceives()
        {
            var router = new ControllerEventRouter();
            var a = new FakeReceiver();
            var b = new FakeReceiver();

            router.Register(a);
            router.Register(b);

            Assert.That(Top(router), Is.SameAs(b), "b was registered last, so it should be on top");
        }

        [Test]
        public void UnregisterTop_RestoresParent()
        {
            var router = new ControllerEventRouter();
            var a = new FakeReceiver();
            var b = new FakeReceiver();

            router.Register(a);
            router.Register(b);
            router.Unregister(b);

            Assert.That(Top(router), Is.SameAs(a), "after popping b, a is active again");
        }

        [Test]
        public void UnregisterMiddle_PreservesOrder()
        {
            var router = new ControllerEventRouter();
            var a = new FakeReceiver();
            var b = new FakeReceiver();
            var c = new FakeReceiver();

            router.Register(a);
            router.Register(b);
            router.Register(c);

            router.Unregister(b); // remove from the MIDDLE
            Assert.That(Top(router), Is.SameAs(c), "c stays on top after middle removal");

            router.Unregister(c);
            Assert.That(Top(router), Is.SameAs(a), "order preserved: a is next, not corrupted");
        }

        [Test]
        public void Unregister_UnknownReceiver_IsNoOp()
        {
            var router = new ControllerEventRouter();
            var a = new FakeReceiver();
            var stranger = new FakeReceiver();

            router.Register(a);
            Assert.DoesNotThrow(() => router.Unregister(stranger));

            Assert.That(router.HasActiveReceiver, Is.True);
            Assert.That(Top(router), Is.SameAs(a), "stack unchanged by unknown unregister");
        }

        [Test]
        public void BalancedNestedRegisterUnregister_ReturnsToEmpty()
        {
            var router = new ControllerEventRouter();
            var results = new FakeReceiver();
            var player = new FakeReceiver();

            // results -> player -> back nav
            router.Register(results);
            router.Register(player);
            router.Unregister(player);
            router.Unregister(results);

            Assert.That(router.HasActiveReceiver, Is.False);
        }

        // The receiver Peek() would return — the same instance HandleButtonPressed routes to.
        // Read off the private stack so we verify push/pop/middle-removal order deterministically
        // without needing a live WPF dispatcher.
        private static IControllerInputReceiver Top(ControllerEventRouter router)
        {
            var field = typeof(ControllerEventRouter)
                .GetField("_receiverStack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var stack = (Stack<IControllerInputReceiver>)field.GetValue(router);
            return stack.Count > 0 ? stack.Peek() : null;
        }
    }
}
