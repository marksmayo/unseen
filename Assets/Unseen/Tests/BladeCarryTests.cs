using NUnit.Framework;
using Unseen.Combat;

namespace Unseen.Tests
{
    /// <summary>
    /// Where the katana is, and whether it can be used yet.
    ///
    /// A blade on the back is not a blade in your hands, and the moment between the two is the
    /// point of this: sprinting across a rooftop and swinging the instant you land should cost
    /// something. It makes the decision to run a real one rather than free movement, which in a
    /// stealth game is most of the tension.
    ///
    /// Server-authoritative, like everything else that decides whether a hit lands. A client that
    /// decided its own blade was ready would be choosing when it can attack.
    /// </summary>
    public sealed class BladeCarryTests
    {
        [Test]
        public void ABladeStartsOnTheBackAndCannotStrike()
        {
            var blade = new BladeCarry();

            Assert.AreEqual(BladeState.Sheathed, blade.State);
            Assert.IsFalse(blade.CanStrike, "nothing is drawn yet");
        }

        [Test]
        public void DrawingTakesTimeAndNothingLandsDuringIt()
        {
            var blade = new BladeCarry();

            blade.WantsDrawn(true);
            blade.Advance(0.1f);

            // Mid-draw. The swing is coming, and it is not here yet - which is the entire reason
            // this class exists. A blade that became usable the instant it was asked for would make
            // sprinting free, and running everywhere would be strictly correct.
            Assert.AreEqual(BladeState.Drawing, blade.State);
            Assert.IsFalse(blade.CanStrike, "a blade halfway off the back cuts nothing");

            blade.Advance(BladeCarry.DrawSeconds);

            Assert.AreEqual(BladeState.Drawn, blade.State);
            Assert.IsTrue(blade.CanStrike);
        }

        [Test]
        public void SprintingPutsItAway()
        {
            var blade = new BladeCarry();

            blade.WantsDrawn(true);
            blade.Advance(BladeCarry.DrawSeconds);
            Assert.IsTrue(blade.CanStrike, "drawn, before the running starts");

            blade.SetSprinting(true);
            blade.Advance(0.01f);

            // Going away immediately, rather than at the end of a sheathe. You cannot run with a
            // live blade in your hand, and the guard goes down the moment you decide to run - that
            // is the trade the player is making.
            Assert.IsFalse(blade.CanStrike, "no guard and no strike while running");

            blade.Advance(BladeCarry.SheatheSeconds);
            Assert.AreEqual(BladeState.Sheathed, blade.State);
        }

        [Test]
        public void ARequestToDrawWhileSprintingIsRefusedUntilTheRunningStops()
        {
            var blade = new BladeCarry();
            blade.SetSprinting(true);

            blade.WantsDrawn(true);
            blade.Advance(BladeCarry.DrawSeconds * 3f);

            Assert.IsFalse(blade.CanStrike, "a sprint keeps the blade on the back");

            // And the intent survives the sprint: a player who asked for the blade while running
            // gets it when they stop, rather than having to ask again at exactly the right moment.
            blade.SetSprinting(false);
            blade.Advance(BladeCarry.DrawSeconds);

            Assert.IsTrue(blade.CanStrike, "stopping lets the draw it already asked for finish");
        }

        [Test]
        public void WantingToStrikeIsWhatStartsTheDraw()
        {
            var blade = new BladeCarry();

            // Pressing attack with the blade away must begin the draw rather than being swallowed.
            // Refusing outright would make the first press of a fight do nothing at all, which
            // reads as the game missing the input rather than as a cost being paid.
            blade.WantsDrawn(true);
            blade.Advance(0.016f);

            Assert.AreEqual(BladeState.Drawing, blade.State,
                "asking to strike starts the blade coming off the back");
        }

        [Test]
        public void TheBladeIsReadyAfterExactlyItsDrawTime()
        {
            var blade = new BladeCarry();
            blade.WantsDrawn(true);

            // Stepped at a frame at a time, as the simulation will. A state machine that loses a
            // tick per transition is a control that feels a fraction behind without ever being
            // visibly wrong, so the total is asserted rather than "eventually".
            float elapsed = 0f;
            while (elapsed < BladeCarry.DrawSeconds)
            {
                blade.Advance(1f / 60f);
                elapsed += 1f / 60f;
            }

            Assert.IsTrue(blade.CanStrike,
                $"a blade asked for should be ready within {BladeCarry.DrawSeconds:0.00}s");
        }
    }
}
