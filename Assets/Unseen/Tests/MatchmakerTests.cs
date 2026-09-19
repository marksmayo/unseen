using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The queue in front of the fleet.
    ///
    /// `ServerAllocator` already answers "which server should this player go to" and packs them so
    /// a fleet does not fill with matches that can never start. What it cannot answer is "what
    /// happens to a player when every server is full", and the honest answer is that they wait -
    /// which means somebody has to hold them, in order, and hand them a server when one appears.
    ///
    /// A ticket rather than a blocking call, because the wait is unbounded and the client is a game
    /// that has to keep drawing frames. The player asks once, gets a ticket, and asks about the
    /// ticket until it turns into an address.
    /// </summary>
    public sealed class MatchmakerTests
    {
        [Test]
        public void APlayerWithRoomWaitingIsSentStraightThere()
        {
            var matchmaker = new Matchmaker(playersPerServer: 2);
            matchmaker.ServerReady("tokyo-1", "10.0.0.7:7770");

            string ticket = matchmaker.Enqueue();
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(ticket, out string address));
            Assert.AreEqual("10.0.0.7:7770", address, "and told where to go, not merely admitted");
        }

        [Test]
        public void APlayerWithNowhereToGoWaitsRatherThanBeingTurnedAway()
        {
            var matchmaker = new Matchmaker(playersPerServer: 1);
            matchmaker.ServerReady("only-one", "10.0.0.7:7770");

            string first = matchmaker.Enqueue();
            string second = matchmaker.Enqueue();
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(first, out string _));

            // The entire reason this exists. "Fleet full, try again" is an answer a player reads as
            // the game being broken, and it arrives at exactly the moment the game is most popular.
            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(second, out string _));
            Assert.AreEqual(1, matchmaker.Waiting);
        }

        [Test]
        public void AServerComingUpMovesTheQueue()
        {
            var matchmaker = new Matchmaker(playersPerServer: 1);

            string ticket = matchmaker.Enqueue();
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(ticket, out string _),
                "nothing has come up yet");

            // The autoscaler keeps a buffer of warm servers precisely so this happens quickly. A
            // queue that only moved when somebody asked again would leave a player waiting in front
            // of an empty server, which is the worst possible use of the machine they are paying
            // for.
            matchmaker.ServerReady("tokyo-2", "10.0.0.8:7770");
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(ticket, out string address));
            Assert.AreEqual("10.0.0.8:7770", address);
        }

        [Test]
        public void TheQueueIsFirstComeFirstServed()
        {
            var matchmaker = new Matchmaker(playersPerServer: 1);

            string first = matchmaker.Enqueue();
            string second = matchmaker.Enqueue();
            string third = matchmaker.Enqueue();

            matchmaker.ServerReady("a", "10.0.0.1:7770");
            matchmaker.Pump();

            // Order is the only fairness a queue has, and it is the one thing a waiting player can
            // check. Somebody who watched two people who arrived after them get in first has been
            // given a reason to believe the game is rigged, whatever the real explanation.
            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(first, out string _));
            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(second, out string _));
            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(third, out string _));

            matchmaker.ServerReady("b", "10.0.0.2:7770");
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(second, out string _),
                "the one who has waited longest goes next");
            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(third, out string _));
        }

        [Test]
        public void PlayersArePackedOntoOneServerBeforeStartingAnother()
        {
            var matchmaker = new Matchmaker(playersPerServer: 2);
            matchmaker.ServerReady("a", "10.0.0.1:7770");
            matchmaker.ServerReady("b", "10.0.0.2:7770");

            string first = matchmaker.Enqueue();
            string second = matchmaker.Enqueue();
            matchmaker.Pump();

            matchmaker.StatusOf(first, out string firstAddress);
            matchmaker.StatusOf(second, out string secondAddress);

            // Spreading is the obvious implementation and produces a fleet of matches that can
            // never start, each costing money while nobody can play. The packing lives in
            // ServerAllocator; this is here so that a queue put in front of it cannot quietly
            // undo it.
            Assert.AreEqual(firstAddress, secondAddress,
                "two players arriving together belong in the same match");
        }

        [Test]
        public void AnUnknownTicketIsNotAnAssignment()
        {
            var matchmaker = new Matchmaker(playersPerServer: 4);
            matchmaker.ServerReady("a", "10.0.0.1:7770");

            // A ticket from a previous run of the service, a typo, or a client inventing one.
            // Anything but Unknown here would hand out a server to a request that never queued.
            Assert.AreEqual(MatchmakerStatus.Unknown, matchmaker.StatusOf("t999", out string _));
            Assert.AreEqual(MatchmakerStatus.Unknown, matchmaker.StatusOf("", out string _));
            Assert.AreEqual(MatchmakerStatus.Unknown, matchmaker.StatusOf(null, out string _));
        }

        [Test]
        public void APlayerLeavingGivesTheirSlotBack()
        {
            var matchmaker = new Matchmaker(playersPerServer: 1);
            matchmaker.ServerReady("only-one", "10.0.0.7:7770");

            string leaver = matchmaker.Enqueue();
            string waiting = matchmaker.Enqueue();
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(waiting, out string _));

            // Without this the occupancy only ever climbs and the fleet reports itself full while
            // every match is empty - the same accumulation as a connection nothing evicts, and for
            // the same reason: leaving is silent, so something has to notice.
            matchmaker.Leave(leaver);
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(waiting, out string address));
            Assert.AreEqual("10.0.0.7:7770", address);
            Assert.AreEqual(MatchmakerStatus.Unknown, matchmaker.StatusOf(leaver, out string _),
                "and the ticket is spent, not left pointing at a match they walked out of");
        }

        [Test]
        public void GivingUpWhileStillInTheQueueLeavesNoGhost()
        {
            var matchmaker = new Matchmaker(playersPerServer: 1);

            string impatient = matchmaker.Enqueue();
            string patient = matchmaker.Enqueue();

            // Somebody closing the game while they wait, which is the commonest thing a queue has
            // to survive. A ticket left in the line would hold a place for a player who is not
            // coming back, and everybody behind them would be handed a server one turn late for
            // the rest of the evening.
            matchmaker.Leave(impatient);

            matchmaker.ServerReady("a", "10.0.0.1:7770");
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Unknown, matchmaker.StatusOf(impatient, out string _),
                "the ticket is gone, not holding a place for somebody who left");

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(patient, out string _),
                "and the next real player goes straight in rather than waiting a turn");

            Assert.AreEqual(0, matchmaker.Waiting, "nobody is left in the queue at all");
        }

        [Test]
        public void AServerThatDiesPutsItsPlayersBackInTheQueue()
        {
            var matchmaker = new Matchmaker(playersPerServer: 2);
            matchmaker.ServerReady("doomed", "10.0.0.1:7770");

            string ticket = matchmaker.Enqueue();
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(ticket, out string _));

            // A pod evicted, a node lost, a crash. The player is holding an address that will never
            // answer, and the worst thing the matchmaker can do is keep telling them to go there.
            // Back in the queue is the only answer that ends with them playing.
            matchmaker.ServerGone("doomed");

            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(ticket, out string _),
                "sent somewhere that no longer exists is the same as not being sent");

            matchmaker.ServerReady("replacement", "10.0.0.2:7770");
            matchmaker.Pump();

            Assert.AreEqual(MatchmakerStatus.Assigned, matchmaker.StatusOf(ticket, out string address));
            Assert.AreEqual("10.0.0.2:7770", address, "and picked up by whatever comes up next");
        }

        [Test]
        public void ADeadServerIsNotHandedToAnybodyElse()
        {
            var matchmaker = new Matchmaker(playersPerServer: 4);
            matchmaker.ServerReady("doomed", "10.0.0.1:7770");
            matchmaker.ServerGone("doomed");

            string ticket = matchmaker.Enqueue();
            matchmaker.Pump();

            // The obvious half of the same rule, and the one that would be missed: removing a
            // server has to take it out of the allocator too, or the fleet goes on cheerfully
            // packing players onto a machine that is gone.
            Assert.AreEqual(MatchmakerStatus.Waiting, matchmaker.StatusOf(ticket, out string _));
        }
    }
}
