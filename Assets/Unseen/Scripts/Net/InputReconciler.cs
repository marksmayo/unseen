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
    /// </summary>
    public sealed class InputReconciler
    {
        /// <summary>One input, kept until the server admits to having processed it.</summary>
        private struct Pending
        {
            public ushort Sequence;
            public float3 Move;
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
        public void Record(ushort sequence, float3 move, float deltaTime)
        {
            _pending.Add(new Pending { Sequence = sequence, Move = move, DeltaTime = deltaTime });

            // Oldest first. Dropping the newest would discard the input the player just made, which
            // is the one they are watching for.
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }

        /// <summary>
        /// Forgets every input the server has confirmed it processed.
        ///
        /// Compared with wrapping in mind: sequence numbers roll over, and a plain less-than would
        /// retire the entire backlog the moment the count passed 65535.
        /// </summary>
        public void AcknowledgeThrough(ushort sequence)
        {
            int keepFrom = 0;

            while (keepFrom < _pending.Count &&
                   !SequenceWindow.IsNewerThan(_pending[keepFrom].Sequence, sequence))
            {
                keepFrom++;
            }

            if (keepFrom > 0) _pending.RemoveRange(0, keepFrom);
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
        public float3 Reconcile(ushort acknowledgedThrough, float3 authoritative,
            Func<float3, float3, float, float3> step)
        {
            AcknowledgeThrough(acknowledgedThrough);
            return Predict(authoritative, step);
        }

        /// <summary>
        /// Where the player is, starting from a position and applying every input not yet
        /// acknowledged.
        /// </summary>
        public float3 Predict(float3 from, Func<float3, float3, float, float3> step)
        {
            float3 at = from;

            for (int i = 0; i < _pending.Count; i++)
                at = step(at, _pending[i].Move, _pending[i].DeltaTime);

            return at;
        }
    }
}
