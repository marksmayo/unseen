namespace Unseen.Combat
{
    /// <summary>Where the katana is.</summary>
    public enum BladeState : byte
    {
        /// <summary>On the back. Nothing can be struck or guarded with it.</summary>
        Sheathed = 0,

        /// <summary>Coming off the back. The cost of having been running.</summary>
        Drawing = 1,

        /// <summary>In hand. Strikes and guards are possible.</summary>
        Drawn = 2,

        /// <summary>Going back over the shoulder, to run.</summary>
        Sheathing = 3
    }

    /// <summary>
    /// Whether the blade is in hand, and how long it takes to get there.
    ///
    /// A blade on the back is not a blade in your hands, and the gap between the two is the whole
    /// point: sprinting across a rooftop and swinging the instant you land should cost something.
    /// It makes running a decision rather than free movement, which in a stealth game is most of
    /// where the tension lives.
    ///
    /// Server-authoritative, like everything else that decides whether a hit lands. A client
    /// deciding its own blade was ready would be choosing when it is allowed to attack.
    /// </summary>
    public sealed class BladeCarry
    {
        /// <summary>
        /// Seconds to bring the blade off the back, and to put it away.
        ///
        /// Starting values, not researched ones. Too long and sprinting becomes a trap nobody uses;
        /// too short and the cost this exists to create is not felt. Only playtesting settles it,
        /// so they are here to be found and changed rather than buried in the logic.
        /// </summary>
        public const float DrawSeconds = 0.35f;

        public const float SheatheSeconds = 0.3f;

        private float _elapsed;
        private bool _wantsDrawn;
        private bool _sprinting;

        public BladeState State { get; private set; } = BladeState.Sheathed;

        /// <summary>Whether the blade can strike or guard this instant.</summary>
        public bool CanStrike => State == BladeState.Drawn;

        /// <summary>
        /// Says whether the player wants the blade in hand - attacking or guarding.
        ///
        /// Remembered rather than obeyed. A request made while sprinting is not thrown away: the
        /// player asked, and they get it the moment they stop, instead of having to ask again at
        /// precisely the right instant. Discarding it would be defensible and would feel like the
        /// game ignoring them.
        /// </summary>
        public void WantsDrawn(bool wanted)
        {
            _wantsDrawn = wanted;
        }

        /// <summary>Whether the agent is running. Running wins over wanting the blade.</summary>
        public void SetSprinting(bool sprinting)
        {
            _sprinting = sprinting;
        }

        /// <summary>Advances the draw or the sheathe by a tick.</summary>
        public void Advance(float deltaTime)
        {
            // Running settles it: you cannot sprint with a live blade, and the guard drops the
            // instant the decision to run is made rather than when an animation finishes. A blade
            // that still blocked on its way to the back would be a free window.
            bool wantDrawn = _wantsDrawn && !_sprinting;

            // Decide the direction first, then move in it - both within this tick.
            //
            // Written as a switch that broke out on each transition, entering a state cost a whole
            // frame before anything moved: a draw took one tick longer than DrawSeconds, and every
            // interruption paid the same toll again. Small, and exactly the kind of thing that
            // makes controls feel a fraction behind the player without being visible in any one
            // moment.
            if (wantDrawn && (State == BladeState.Sheathed || State == BladeState.Sheathing))
            {
                Begin(BladeState.Drawing);
            }
            else if (!wantDrawn && (State == BladeState.Drawn || State == BladeState.Drawing))
            {
                Begin(BladeState.Sheathing);
            }

            if (State == BladeState.Drawing)
            {
                if (Step(deltaTime, DrawSeconds)) Begin(BladeState.Drawn);
            }
            else if (State == BladeState.Sheathing)
            {
                if (Step(deltaTime, SheatheSeconds)) Begin(BladeState.Sheathed);
            }
        }

        /// <summary>How far through the current draw or sheathe, 0 to 1. For the visual.</summary>
        public float Progress
        {
            get
            {
                switch (State)
                {
                    case BladeState.Drawing: return UnityEngine.Mathf.Clamp01(_elapsed / DrawSeconds);
                    case BladeState.Sheathing: return UnityEngine.Mathf.Clamp01(_elapsed / SheatheSeconds);
                    case BladeState.Drawn: return 1f;
                    default: return 0f;
                }
            }
        }

        private void Begin(BladeState next)
        {
            State = next;
            _elapsed = 0f;
        }

        private bool Step(float deltaTime, float duration)
        {
            _elapsed += deltaTime;
            return _elapsed >= duration;
        }
    }
}
