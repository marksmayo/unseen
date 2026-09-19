using NUnit.Framework;
using Unseen.Combat;

namespace Unseen.Tests
{
    /// <summary>
    /// What happens to an attack pressed before the sword is out.
    ///
    /// The bug this exists to end: the press was thrown away. Wanting to strike starts the draw,
    /// the draw takes 0.35 s, and the swing was gated on the blade being ready *on the frame of the
    /// press* - which it never is, because the press is what started the draw. The attack was then
    /// refused on every frame after that too, since a held button produces no second rising edge.
    ///
    /// So the first click of any fight did nothing, and so did the first click after sprinting,
    /// because running puts the sword away. You had to release and click again, after the draw you
    /// could not see. The comment above the code said the swing "simply arrives when the blade
    /// does", which was the intention and not what happened.
    ///
    /// A buffer rather than a flag, because it has to expire. A click while sprinting across a roof
    /// should not fire a swing at nobody four seconds later when you finally stop.
    /// </summary>
    public sealed class AttackBufferTests
    {
        [Test]
        public void AnAttackWithTheBladeAlreadyOutFiresAtOnce()
        {
            var buffer = new AttackBuffer();

            Assert.IsTrue(buffer.Tick(pressed: true, canStrike: true, now: 1f),
                "the ordinary case, and it must not be made worse by any of the below");
        }

        [Test]
        public void AnAttackPressedWhileTheBladeIsComingOutStillLands()
        {
            var buffer = new AttackBuffer();

            // The press that starts the draw. Nothing can swing yet.
            Assert.IsFalse(buffer.Tick(pressed: true, canStrike: false, now: 0f));

            // Held down through the draw, producing no further rising edges. This is where every
            // first attack in the game was being lost.
            for (float t = 1f / 60f; t < 0.35f; t += 1f / 60f)
                Assert.IsFalse(buffer.Tick(pressed: false, canStrike: false, now: t),
                    $"nothing to swing with yet at {t:0.000}s");

            Assert.IsTrue(buffer.Tick(pressed: false, canStrike: true, now: 0.36f),
                "and the swing arrives with the blade, which is what the code always claimed");
        }

        [Test]
        public void ABufferedAttackFiresOnlyOnce()
        {
            var buffer = new AttackBuffer();

            buffer.Tick(pressed: true, canStrike: false, now: 0f);

            Assert.IsTrue(buffer.Tick(pressed: false, canStrike: true, now: 0.36f), "fires");

            // One press, one swing. Without clearing it, holding the button through a draw would
            // buy a swing every tick for as long as the blade stayed out.
            Assert.IsFalse(buffer.Tick(pressed: false, canStrike: true, now: 0.37f),
                "a single press must not become a stream of swings");
        }

        [Test]
        public void AnOldPressIsForgottenRatherThanHonouredLate()
        {
            var buffer = new AttackBuffer();

            buffer.Tick(pressed: true, canStrike: false, now: 0f);

            // Sprinting keeps the sword away for as long as you run. A click at the start of a roof
            // chase must not turn into a swing when you stop at the other end - that is a player
            // attacking somebody they never chose to attack, which reads as the game doing things
            // by itself.
            Assert.IsFalse(buffer.Tick(pressed: false, canStrike: true,
                    now: AttackBuffer.BufferSeconds + 0.1f),
                "a press this old was about a moment that has passed");
        }

        [Test]
        public void TheBufferIsLongEnoughToCoverADraw()
        {
            // The number has one job: a click that starts a draw must still be honoured when that
            // draw finishes. A buffer shorter than the draw would leave the original bug in place
            // for the exact case it was written for.
            Assert.GreaterOrEqual(AttackBuffer.BufferSeconds, BladeCarry.DrawSeconds,
                "a buffer shorter than a draw cannot survive one");
        }

        [Test]
        public void ReleasingAndPressingAgainStillWorks()
        {
            var buffer = new AttackBuffer();

            Assert.IsTrue(buffer.Tick(pressed: true, canStrike: true, now: 1f), "first swing");
            Assert.IsFalse(buffer.Tick(pressed: false, canStrike: true, now: 1.1f), "button up");
            Assert.IsTrue(buffer.Tick(pressed: true, canStrike: true, now: 1.2f), "second swing");
        }
    }
}
