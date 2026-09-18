using NUnit.Framework;
using Unseen.Core;

namespace Unseen.Tests
{
    /// <summary>
    /// Which parts of the simulation a process is responsible for.
    ///
    /// The bug this exists to end: `_sim.Advance(dt)` ran in every mode, so a pure client was
    /// running its own MatchDirector, its own sixty-four bots, its own interest management and its
    /// own mist - a complete second game, on the player's machine, agreeing with the server only by
    /// coincidence. It was not merely wasteful. A client that simulates its own world has no reason
    /// to believe the one it is sent, and prediction has nothing to predict, because the local
    /// ninja was already moving under its own authority.
    ///
    /// Named by responsibility rather than by mode so the bootstrap reads as what it is doing
    /// rather than as a list of enum comparisons, and so the answer for a mode is in one place
    /// instead of scattered across twenty registration lines.
    /// </summary>
    public sealed class SimProfileTests
    {
        [Test]
        public void AClientMovesItsOwnNinjaAndNothingElse()
        {
            SimProfile client = SimProfile.For(LaunchMode.Client);

            // The one thing it must still do, and it must do it with the server's own motor: a
            // prediction that is a second implementation of movement disagrees with the first, and
            // every correction then snaps the player somewhere they did not expect.
            Assert.IsTrue(client.MovesAgents, "a client still moves its own ninja");

            Assert.IsFalse(client.OwnsTheMatch, "the match, the bots and the mist belong to the server");
            Assert.IsFalse(client.ResolvesPerception,
                "who can see whom is the server's answer - deciding it locally is what a wallhack is");
            Assert.IsFalse(client.Replicates, "it has nobody to replicate to");
        }

        [Test]
        public void OfflinePracticeIsAServerWithNobodyConnectedToIt()
        {
            SimProfile offline = SimProfile.For(LaunchMode.OfflinePractice);

            // Easy to get backwards, because "offline" sounds like the opposite of "server". It is
            // the full authoritative loop with one human in it, and it exercises the whole
            // replication path locally, which is most of what it is for.
            Assert.IsTrue(offline.OwnsTheMatch);
            Assert.IsTrue(offline.ResolvesPerception, "sixty-three bots have to be able to see");
            Assert.IsTrue(offline.MovesAgents);
            Assert.IsTrue(offline.Replicates);
        }

        [Test]
        public void BothServerModesOwnEverything()
        {
            SimProfile listen = SimProfile.For(LaunchMode.ListenServer);
            SimProfile dedicated = SimProfile.For(LaunchMode.DedicatedServer);

            Assert.IsTrue(listen.OwnsTheMatch);
            Assert.IsTrue(listen.ResolvesPerception);
            Assert.IsTrue(listen.MovesAgents);
            Assert.IsTrue(listen.Replicates);

            Assert.IsTrue(dedicated.OwnsTheMatch);
            Assert.IsTrue(dedicated.ResolvesPerception);
            Assert.IsTrue(dedicated.MovesAgents);
            Assert.IsTrue(dedicated.Replicates);
        }

        [Test]
        public void AnUnknownModeIsTreatedAsAClient()
        {
            // Failing closed. A mode this does not recognise is one that arrived from a launch
            // argument, and granting it authority by default would hand the simulation to whatever
            // a command line happened to say.
            SimProfile unknown = SimProfile.For((LaunchMode)99);

            Assert.IsFalse(unknown.OwnsTheMatch);
            Assert.IsFalse(unknown.ResolvesPerception);
        }
    }
}
