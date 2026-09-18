using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Unseen.Net
{
    /// <summary>
    /// Keeps a client's own movement responsive without letting it disagree with the server.
    ///
    /// A client that waits for the server before moving has the round trip in its legs, and at
    /// eighty milliseconds that is the difference between a game that responds and one that argues.
    /// So the client applies each input the moment it is made and remembers it; when the server's
    /// authoritative position arrives - always describing a moment already in the past - the client
    /// rewinds to it and replays every input the server had not yet seen.
    ///
    /// The movement step is handed in rather than reimplemented here. That is the whole discipline
    /// of this class: a prediction that is a *second* implementation of movement will drift from the
    /// first, and every correction then snaps the player somewhere they did not expect. One step
    /// function, used by both sides, cannot disagree with itself.
    ///
    /// Generic in the input for the same reason. Where a step lands depends on much more than a
    /// direction - sprint and stance change the speed, a jump leaves the ground, and the move is
    /// relative to the yaw it was made at - so the replay has to carry whatever the real motor
    /// reads. Keeping that a type parameter rather than naming the game's own intent keeps the
    /// class testable with nothing but arithmetic.
    /// </summary>
    public sealed class InputReconciler<TInput>
    {
        /// <summary>One input, kept until the server admits to having processed it.</summary>
        private struct Pending
        {
            public uint Sequence;
            public TInput Input;
            public float DeltaTime;
        }

        private readonly List<Pending> _pending = new List<Pending>();

        /// <summary>
        /// Most inputs kept while the server says nothing.
        ///
        /// Two seconds at sixty a second. A server that has gone quiet - a lag spike rather than a
        /// disconnect - acknowledges nothing, and without a limit every input since is kept and
        /// replayed each frame. The same shape as a connection nothing evicts: what would clear this
        /// depends on a message that may never arrive, so it needs a bound of its own. An input from
        /// two seconds ago will not be usefully reconciled in any case.
        /// </summary>
        public const int MaxPending = 120;

        /// <summary>How many inputs are still unacknowledged.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Remembers an input the client has just applied locally.</summary>
        public void Record(uint sequence, TInput input, float deltaTime)
        {
            _pending.Add(new Pending { Sequence = sequence, Input = input, DeltaTime = deltaTime });

            // Oldest first. Dropping the newest would discard the input the player just made, which
            // is the one they are watching for.
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }

        /// <summary>
        /// Forgets every input the server has confirmed it processed.
        ///
        /// Thirty-two bits, the same as the wire and the same as the intent itself. Held in sixteen
        /// it would have needed narrowing at every call site, identically at both ends, which is how
        /// a truncation bug arrives that nobody can reproduce because it needs eighteen minutes of
        /// continuous play to appear.
        ///
        /// Compared by signed difference regardless, so that a count which does eventually wrap does
        /// not retire the whole backlog at once. It costs nothing to be right about.
        /// </summary>
        public void AcknowledgeThrough(uint sequence)
        {
            int keepFrom = 0;

            while (keepFrom < _pending.Count &&
                   !IsNewerThan(_pending[keepFrom].Sequence, sequence))
            {
                keepFrom++;
            }

            if (keepFrom > 0) _pending.RemoveRange(0, keepFrom);
        }

        /// <summary>
        /// Whether one input sequence comes after another, allowing for the count wrapping.
        ///
        /// The subtraction is done in unsigned arithmetic and read as signed, which is what makes
        /// the comparison survive the roll-over: a plain greater-than would decide that sequence 1
        /// is older than 4,294,967,295 and retire everything still in flight.
        /// </summary>
        private static bool IsNewerThan(uint sequence, uint than)
        {
            return (int)(sequence - than) > 0;
        }

        /// <summary>
        /// Takes the server's authoritative position and rebuilds the local one on top of it.
        ///
        /// The server's answer always describes a moment already past - it is a round trip old by
        /// the time it lands - so it is not where the player should be drawn, it is where the replay
        /// starts. Everything the server had not yet processed is applied again from there.
        ///
        /// The correction wins outright. Prediction is a guess made to hide latency, and when the
        /// truth arrives the guess is worth nothing: blending the two would leave the player at a
        /// position neither side believes, and every prediction after it compounds from somewhere
        /// that was never real. State snaps; smoothing belongs in what is drawn, not in what is
        /// believed.
        /// </summary>
        public float3 Reconcile(uint acknowledgedThrough, float3 authoritative,
            Func<float3, TInput, float, float3> step)
        {
            AcknowledgeThrough(acknowledgedThrough);
            return Predict(authoritative, step);
        }

        /// <summary>
        /// Where the player is, starting from a position and applying every input not yet
        /// acknowledged.
        /// </summary>
        public float3 Predict(float3 from, Func<float3, TInput, float, float3> step)
        {
            float3 at = from;

            for (int i = 0; i < _pending.Count; i++)
                at = step(at, _pending[i].Input, _pending[i].DeltaTime);

            return at;
        }
    }
}
