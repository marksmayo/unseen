using NUnit.Framework;
using Unity.Mathematics;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// Client-side prediction and reconciliation.
    ///
    /// The problem this solves: a client that waits for the server before moving feels like it has
    /// the round trip in its legs, and at 80 ms that is the difference between a game that responds
    /// and one that argues. So the client applies its own input immediately and keeps it, and when
    /// the server's authoritative answer arrives it rewinds to that and replays everything the
    /// server had not yet seen.
    ///
    /// The whole thing is arithmetic over a list of inputs, so it is tested without a network, a
    /// simulation or a frame. What it is not is a second movement implementation - the same step
    /// function is handed in, because two versions of movement that disagree is a player who
    /// teleports whenever the server speaks.
    /// </summary>
    public sealed class InputReconcilerTests
    {
        /// <summary>A stand-in for the movement step: position plus the move, times the step.</summary>
        private static float3 Step(float3 from, float3 move, float dt) => from + move * dt;

        [Test]
        public void PredictionMovesImmediatelyWithoutWaitingForTheServer()
        {
            var reconciler = new InputReconciler();

            reconciler.Record(1, new float3(1f, 0f, 0f), 0.1f);
            float3 predicted = reconciler.Predict(float3.zero, Step);

            Assert.AreEqual(0.1f, predicted.x, 1e-4f,
                "the client should already have moved before the server has heard of it");
        }

        [Test]
        public void AcknowledgedInputsAreForgotten()
        {
            var reconciler = new InputReconciler();

            reconciler.Record(1, new float3(1f, 0f, 0f), 0.1f);
            reconciler.Record(2, new float3(1f, 0f, 0f), 0.1f);
            reconciler.Record(3, new float3(1f, 0f, 0f), 0.1f);

            reconciler.AcknowledgeThrough(2);

            // Without this the list only grows, and Predict replays all of it every frame: at sixty
            // inputs a second a client that has been connected five minutes replays eighteen
            // thousand steps per frame. It degrades slowly and looks like the game getting heavier
            // the longer you play, which is not a shape anybody debugs as a network problem.
            Assert.AreEqual(1, reconciler.PendingCount, "only the unacknowledged input remains");
        }

        [Test]
        public void ReconcilingRewindsToTheServerAndReplaysWhatItHasNotSeen()
        {
            var reconciler = new InputReconciler();

            reconciler.Record(1, new float3(1f, 0f, 0f), 0.1f);
            reconciler.Record(2, new float3(1f, 0f, 0f), 0.1f);
            reconciler.Record(3, new float3(1f, 0f, 0f), 0.1f);

            // The server has processed up to input 2 and says the player is at x=0.2 - which is
            // where the client also thinks it was after two steps, so there is no disagreement to
            // resolve. Input 3 it has not seen yet.
            float3 corrected = reconciler.Reconcile(2, new float3(0.2f, 0f, 0f), Step);

            Assert.AreEqual(0.3f, corrected.x, 1e-4f,
                "authoritative position plus the one input the server has not processed");
            Assert.AreEqual(1, reconciler.PendingCount, "and the acknowledged inputs are retired");
        }

        [Test]
        public void AServerCorrectionWins()
        {
            var reconciler = new InputReconciler();

            reconciler.Record(1, new float3(1f, 0f, 0f), 0.1f);
            reconciler.Record(2, new float3(1f, 0f, 0f), 0.1f);

            // The server disagrees - the player walked into something the client did not know about,
            // or was pushed. Its answer is the one that counts: prediction is a guess made to hide
            // latency, and the moment the truth arrives the guess is worth nothing.
            float3 corrected = reconciler.Reconcile(1, new float3(5f, 0f, 0f), Step);

            Assert.AreEqual(5.1f, corrected.x, 1e-4f,
                "rebuilt from where the server says, not from where the client hoped");
        }

        [Test]
        public void TheBacklogCannotGrowWithoutBound()
        {
            var reconciler = new InputReconciler();

            // A server that has gone quiet - a lag spike rather than a disconnect - acknowledges
            // nothing, and every input since is kept. Same shape as a connection nothing evicts: the
            // thing that would clear this depends on a message that may never arrive, so it needs a
            // limit of its own. An input from four seconds ago will not be usefully reconciled
            // anyway.
            for (int i = 1; i <= 5000; i++)
                reconciler.Record((ushort)i, new float3(1f, 0f, 0f), 1f / 60f);

            Assert.LessOrEqual(reconciler.PendingCount, InputReconciler.MaxPending,
                "the backlog must be bounded even when the server never answers");
        }
    }
}
