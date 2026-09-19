namespace Unseen.Combat
{
    /// <summary>
    /// Holds an attack that was asked for before the sword was out, and spends it when it is.
    ///
    /// Wanting to strike is what starts the draw, and the draw takes a third of a second. The swing
    /// used to be gated on the blade being ready on the very frame of the press - which it never
    /// is, because the press is what started the draw - and the attack was refused on every frame
    /// afterwards too, since a held button produces no second rising edge.
    ///
    /// So the first click of any fight did nothing. So did the first click after sprinting, because
    /// running puts the sword away. The player had to release and click again, after a draw they
    /// had no way to see, and the game read as a sword that ignored about half of what you asked
    /// of it.
    ///
    /// A buffer rather than a flag, because an unspent press has to expire. A click while sprinting
    /// across a roof must not become a swing at nobody four seconds later when you finally stop:
    /// an attack the player did not choose, at a moment they were not thinking about it, reads as
    /// the game acting on its own.
    /// </summary>
    public sealed class AttackBuffer
    {
        /// <summary>
        /// How long an unspent press is worth honouring.
        ///
        /// Has to cover a draw, or it would fail at exactly the thing it was written for. The
        /// margin beyond that is deliberately small - this is a buffer for a press that arrived a
        /// moment too early, not a queue of intentions.
        /// </summary>
        public const float BufferSeconds = BladeCarry.DrawSeconds + 0.15f;

        private bool _pending;
        private float _pendingSince;

        /// <summary>Whether an attack is waiting for a blade. For diagnostics.</summary>
        public bool HasPending => _pending;

        /// <summary>
        /// Call once per tick. Returns true on the tick a swing should begin.
        ///
        /// <paramref name="pressed"/> is the rising edge of the attack button, not whether it is
        /// held: a held button is one attack, and the buffer must not turn it into a stream.
        /// </summary>
        public bool Tick(bool pressed, bool canStrike, float now)
        {
            if (pressed)
            {
                // Ready: spend it immediately and remember nothing. This is the ordinary case and
                // it must not be slowed down by the machinery that exists for the other one.
                if (canStrike)
                {
                    _pending = false;
                    return true;
                }

                _pending = true;
                _pendingSince = now;
                return false;
            }

            if (!_pending) return false;

            // Too old to be about anything the player is still thinking about.
            if (now - _pendingSince > BufferSeconds)
            {
                _pending = false;
                return false;
            }

            if (!canStrike) return false;

            // Cleared as it is spent, so one press buys one swing however long the blade stays out.
            _pending = false;
            return true;
        }

        /// <summary>Forgets any waiting attack. For a death, a respawn or a new match.</summary>
        public void Clear()
        {
            _pending = false;
        }
    }
}
