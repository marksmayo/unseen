using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// What a server says about itself, and what it does when told to stop.
    ///
    /// Both halves of "a server update must not kill matches in progress". An orchestrator decides
    /// whether a pod is healthy and when to replace it, and it can only act on what the process
    /// tells it - so a server that answers "alive" while its simulation has stopped will be left in
    /// the fleet forever, and one that exits the moment it is asked takes sixty-three other people
    /// out of a match they were enjoying.
    ///
    /// Plain state rather than anything that touches Kubernetes. The decisions here are about when
    /// a server is unwell and when it may leave, and both should be testable without a cluster.
    /// </summary>
    public sealed class ServerLifecycleTests
    {
        [Test]
        public void AServerThatIsTickingIsHealthy()
        {
            var life = new ServerLifecycle();

            life.Ticked(now: 1f);

            Assert.IsTrue(life.IsHealthy(now: 1.2f), "a server that just ticked is alive");
        }

        [Test]
        public void AServerWhoseSimulationHasStoppedIsNotHealthy()
        {
            var life = new ServerLifecycle();
            life.Ticked(now: 1f);

            // The distinction the whole health check exists for. A process that is running but not
            // simulating is the worst case for everybody in it: the port still accepts, the
            // orchestrator sees a live pod, and sixty-four players stand frozen in a town until
            // somebody notices by hand. "The process exists" is not health.
            Assert.IsFalse(life.IsHealthy(now: 1f + ServerLifecycle.StallSeconds + 0.1f),
                "a simulation that has stopped is a server that should be replaced");
        }

        [Test]
        public void AFreshServerIsGivenTimeToStartBeforeBeingJudged()
        {
            var life = new ServerLifecycle();

            // Generating the town takes most of a minute. A health check that failed during that
            // would have the orchestrator kill and restart every server it ever started, forever,
            // and the fleet would never come up at all.
            Assert.IsTrue(life.IsHealthy(now: 30f), "still starting is not yet unwell");
        }

        [Test]
        public void AskedToStopItDrainsRatherThanDying()
        {
            var life = new ServerLifecycle();
            life.Ticked(now: 10f);

            life.Drain(now: 10f);

            // The entire point. A rolling deploy sends this to every server in turn, and a server
            // that took it literally would end a match in progress for everybody in it - the update
            // arriving as a crash, from the player's side.
            Assert.IsTrue(life.IsDraining, "it knows it is going");
            Assert.IsFalse(life.ShouldExit(now: 10f, matchInProgress: true),
                "but not while there is a match to finish");
        }

        [Test]
        public void ADrainingServerTakesNoNewPlayers()
        {
            var life = new ServerLifecycle();
            life.Ticked(now: 10f);

            Assert.IsTrue(life.AcceptsPlayers, "ordinarily, yes");

            life.Drain(now: 10f);

            // Seating somebody on a server that is about to go is worse than making them wait: they
            // load for a minute, play for two, and are dropped by a deploy they had no part in.
            Assert.IsFalse(life.AcceptsPlayers, "a server on its way out is not a place to be sent");
        }

        [Test]
        public void ItLeavesWhenTheMatchIsOver()
        {
            var life = new ServerLifecycle();
            life.Ticked(now: 10f);
            life.Drain(now: 10f);

            Assert.IsFalse(life.ShouldExit(now: 20f, matchInProgress: true), "not yet");
            Assert.IsTrue(life.ShouldExit(now: 20f, matchInProgress: false),
                "and the moment the match ends, it goes");
        }

        [Test]
        public void ItDoesNotWaitForeverForAMatchThatWillNotEnd()
        {
            var life = new ServerLifecycle();
            life.Ticked(now: 10f);
            life.Drain(now: 10f);

            // A match nobody can win - two players who will not find each other, or a bug in the
            // mist - would hold a pod open against a deploy indefinitely. Kubernetes answers that
            // with SIGKILL after its grace period, which is exactly the abrupt end draining exists
            // to avoid, so the server should leave on its own terms first.
            Assert.IsTrue(life.ShouldExit(now: 10f + ServerLifecycle.DrainSeconds + 1f,
                    matchInProgress: true),
                "a drain has a deadline, or the orchestrator enforces one less kindly");
        }

        [Test]
        public void DrainingTwiceDoesNotRestartTheClock()
        {
            var life = new ServerLifecycle();
            life.Ticked(now: 10f);

            life.Drain(now: 10f);
            life.Drain(now: 10f + ServerLifecycle.DrainSeconds * 0.9f);

            // An orchestrator repeating itself, or two signals arriving. If the second reset the
            // deadline, a server could be kept alive indefinitely by something that was trying to
            // shut it down.
            Assert.IsTrue(life.ShouldExit(now: 10f + ServerLifecycle.DrainSeconds + 1f,
                    matchInProgress: true),
                "the deadline is from the first request, not the last");
        }
    }
}
