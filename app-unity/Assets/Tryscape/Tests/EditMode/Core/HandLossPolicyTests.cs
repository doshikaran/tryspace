using NUnit.Framework;
using Tryscape.Application.Rehearsal;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>S05.3 hand-tracking loss: pause once after a 0.5 s grace, never without prior tracking.</summary>
    public sealed class HandLossPolicyTests
    {
        [Test]
        public void NeverFiresWhenHandsWereNeverTracked()
        {
            var policy = new HandLossPolicy();
            for (var i = 0; i < 100; i++)
                Assert.IsFalse(policy.Update(false, 0.1), "Editor or controllers-only: no hands, no pause");
            Assert.IsFalse(policy.HandsLost);
        }

        [Test]
        public void FiresOnceAfterTheGracePeriodAndRearmsWhenHandsReturn()
        {
            var policy = new HandLossPolicy();
            Assert.IsFalse(policy.Update(true, 0.016));
            Assert.IsFalse(policy.Update(false, 0.3), "0.3 s < 0.5 s grace");
            Assert.IsTrue(policy.HandsLost);
            Assert.IsTrue(policy.Update(false, 0.2), "0.5 s reached: pause");
            Assert.IsFalse(policy.Update(false, 5.0), "fires once per loss");
            Assert.IsFalse(policy.Update(true, 0.016), "hands back: never auto-resumes");
            Assert.IsFalse(policy.HandsLost);
            Assert.IsFalse(policy.Update(false, 0.49));
            Assert.IsTrue(policy.Update(false, 0.01), "a second loss pauses again");
        }

        [Test]
        public void ABriefDropoutDoesNotPause()
        {
            var policy = new HandLossPolicy();
            policy.Update(true, 0.016);
            Assert.IsFalse(policy.Update(false, 0.4));
            Assert.IsFalse(policy.Update(true, 0.016), "back within the grace period");
            Assert.IsFalse(policy.Update(false, 0.4), "grace restarts from zero");
        }
    }
}
