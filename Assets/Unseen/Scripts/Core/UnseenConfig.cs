using System;
using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// Single tuning surface for the whole simulation. Systems never hard-code numbers.
    /// A code default exists so the game boots with no asset assigned.
    /// </summary>
    [CreateAssetMenu(menuName = "Unseen/Config", fileName = "UnseenConfig")]
    public sealed class UnseenConfig : ScriptableObject
    {
        [Serializable]
        public sealed class NetworkSection
        {
            [Tooltip("Base spatial tick for roaming entities.")]
            public int BaseTickRate = 20;

            [Tooltip("Tick rate applied inside an active combat pocket.")]
            public int CombatTickRate = 60;

            [Tooltip("Radius of a combat pocket. Two hostile agents this close both go hot.")]
            public float CombatPocketRadius = 18f;

            [Tooltip("How long an agent stays hot after its last hostile contact.")]
            public float CombatPocketLinger = 6f;

            [Tooltip("Entities beyond this range are never replicated, visible or not.")]
            public float ReplicationRadius = 120f;

            [Tooltip("Position quantisation step in metres for snapshot encoding.")]
            public float PositionQuantum = 0.01f;

            public int MaxPlayers = 64;
        }

        [Serializable]
        public sealed class InterestSection
        {
            [Tooltip("Edge length of one interest voxel. Should exceed the widest per-tick move.")]
            public float VoxelSize = 16f;

            [Tooltip("Line-of-sight checks budgeted per server tick. Excess pairs reuse cached results.")]
            public int LosRaycastBudget = 3000;

            [Tooltip("How long a line-of-sight result stays trusted before it must be re-cast.")]
            public float LosCacheLifetime = 0.15f;

            [Tooltip("Grace period after losing sight during which the target keeps replicating.")]
            public float VisibilityLinger = 0.35f;

            [Tooltip("Horizontal field of view used for the server-side frustum gate, in degrees.")]
            public float ViewFieldOfView = 100f;

            [Tooltip("Vertical half-angle for the frustum gate, in degrees.")]
            public float ViewPitchTolerance = 70f;

            [Tooltip("Furthest an agent can be resolved by eye in clear air.")]
            public float MaxSightRange = 90f;

            [Tooltip("Extra sight range granted by a night-vision elixir.")]
            public float NightVisionBonus = 35f;
        }

        [Serializable]
        public sealed class StealthSection
        {
            [Tooltip("Stealth index at pitch black, with no light source in range.")]
            public float AmbientHiddenFloor = 0.85f;

            [Tooltip("Samples taken around the torso when testing light occlusion.")]
            public int LightSamples = 3;

            [Tooltip("Hidden bonus while crouched.")]
            public float CrouchBonus = 0.12f;

            [Tooltip("Hidden bonus while prone.")]
            public float ProneBonus = 0.2f;

            [Tooltip("Hidden penalty while sprinting - motion catches the eye.")]
            public float SprintPenalty = 0.18f;

            [Tooltip("Hidden bonus while inside a smoke cloud.")]
            public float SmokeBonus = 0.6f;

            [Tooltip("Stealth index at or above which an agent is functionally invisible past a few metres.")]
            public float ConcealedThreshold = 0.72f;

            [Tooltip("How much a high stealth index shortens the range at which an observer resolves you.")]
            public float StealthRangeScale = 0.85f;

            [Tooltip("Seconds for the stealth index to move to a new target value.")]
            public float SmoothingTime = 0.25f;
        }

        [Serializable]
        public sealed class AudioSection
        {
            [Tooltip("Occluders sampled along one source-to-listener path.")]
            public int MaxOccludersPerPath = 6;

            [Tooltip("Perceived intensity below which a sound is dropped entirely.")]
            public float AudibilityFloor = 0.06f;

            [Tooltip("Metres of positional error injected per unit of occlusion.")]
            public float MuffledPositionError = 6f;

            [Tooltip("Loudness of one standing footstep, and its audible radius.")]
            public float FootstepLoudness = 1f;

            public float FootstepRadius = 26f;
            public float SprintLoudnessScale = 2.1f;
            public float CrouchLoudnessScale = 0.35f;
            public float ProneLoudnessScale = 0.15f;

            [Tooltip("Multiplier applied to footstep radius by soft-soled tabi boots.")]
            public float TabiBootsRadiusScale = 0.5f;

            [Tooltip("Seconds between footsteps at a full run.")]
            public float StrideInterval = 0.38f;
        }

        [Serializable]
        public sealed class MovementSection
        {
            public float WalkSpeed = 3.6f;
            public float SprintSpeed = 6.8f;

            [Tooltip("Water depth at which wading is at its slowest, in metres. Deeper than this " +
                     "costs nothing extra - you are already pushing your whole body through it.")]
            public float WadeFullDepth = 1.2f;

            [Tooltip("Fraction of normal speed in water at WadeFullDepth.")]
            [Range(0.15f, 1f)] public float WadeSlowest = 0.45f;

            [Tooltip("Water deeper than this stops anyone sprinting. You can splash through a " +
                     "puddle at a run; you cannot run through your own thighs.")]
            public float WadeSprintDepth = 0.4f;
            public float CrouchSpeed = 1.7f;
            public float ProneSpeed = 0.85f;
            public float RafterSpeed = 1.4f;
            public float Acceleration = 22f;
            public float AirAcceleration = 6f;
            public float Gravity = -22f;
            public float JumpVelocity = 6.2f;
            public float TerminalVelocity = -45f;
            public float StandHeight = 1.8f;
            public float CrouchHeight = 1.15f;
            public float ProneHeight = 0.6f;
            public float Radius = 0.32f;
            public float EyeOffset = -0.12f;

            [Tooltip("Max wall-climb duration before stamina forces a drop.")]
            public float WallClimbDuration = 2.4f;

            public float WallClimbSpeed = 2.6f;
            public float WallRunSpeed = 6.4f;
            public float WallRunDuration = 1.8f;
            public float LedgeGrabReach = 0.75f;
            public float MantleDuration = 0.45f;
            public float GrappleRange = 34f;
            public float GrappleReelSpeed = 16f;
            public float GrappleCooldown = 3.5f;

            [Tooltip("Extra noise the grapple makes when an enemy is nearby.")]
            public float GrappleNoiseLoudness = 2.6f;

            public float FallDamageThreshold = -14f;
            public float FallDamagePerUnit = 3.4f;
        }

        [Serializable]
        public sealed class CombatSection
        {
            public float MeleeRange = 2.15f;
            public float MeleeArcDegrees = 70f;
            public float LightDamage = 34f;
            public float HeavyDamage = 58f;
            public float LightWindup = 0.22f;
            public float HeavyWindup = 0.42f;
            public float Recovery = 0.35f;

            [Tooltip("Base parry window. Widened per-client by measured latency.")]
            public float ParryWindowBase = 0.15f;

            [Tooltip("Upper bound on the latency-compensated parry window.")]
            public float ParryWindowMax = 0.2f;

            [Tooltip("Fraction of measured RTT added to the parry window.")]
            public float ParryLatencyCompensation = 0.5f;

            public float StaggerDuration = 0.9f;
            public float GuardBreakDuration = 1.4f;
            public float BlockedDamageScale = 0.15f;

            [Tooltip("Damage multiplier for hitting the zone the guard does not cover.")]
            public float WrongZoneDamageScale = 1.35f;

            [Tooltip("Duration of the lockstep silent takedown.")]
            public float TakedownDuration = 1.5f;

            [Tooltip("Max angle behind the victim that still counts as a rear takedown.")]
            public float TakedownRearArc = 110f;

            public float TakedownRange = 1.6f;

            [Tooltip("Vertical drop that qualifies as an above-takedown.")]
            public float TakedownAboveHeight = 2.2f;

            [Tooltip("An agent that has seen the attacker within this window is not takedown-eligible.")]
            public float AwarenessMemory = 2.5f;

            public float MaxHealth = 100f;
        }

        [Serializable]
        public sealed class MatchSection
        {
            public int TargetEntityCount = 64;
            public float InfiltrationDuration = 45f;
            [Tooltip("Grace before the first circle moves. Long enough to loot, short enough that " +
                     "the match has started happening.")]
            public float FirstZoneDelay = 75f;

            [Tooltip("How long a circle sits still before the next one closes.")]
            public float ZoneHoldDuration = 40f;

            [Tooltip("How long a circle takes to close. Total match length is roughly " +
                     "FirstZoneDelay + ZoneStages * (hold + close): about ten minutes at these " +
                     "values, against nearly twenty at the originals.")]
            public float ZoneCloseDuration = 35f;

            public int ZoneStages = 7;

            [Tooltip("Upper bound on the first circle. The mist starts at whichever is smaller, " +
                     "this or the map radius, so a small level is never given a circle it cannot " +
                     "fill.")]
            public float InitialZoneRadius = 620f;
            public float FinalZoneRadius = 28f;

            [Tooltip("Radius the last circle collapses to, in metres.\n\n" +
                     "The final stage used to be the end of it: the circle reached its final " +
                     "radius and then held there for the rest of the match, because the Final " +
                     "phase had no behaviour. Two players content to sit still in a 28 m circle " +
                     "were never made to fight, and now that the bamboo stands on the boundary it " +
                     "visibly stopped closing, which reads as the mechanic breaking.")]
            public float FinalCollapseRadius = 5f;

            [Tooltip("Seconds the last circle takes to collapse. Slow, so it is a tightening " +
                     "noose rather than a sudden squeeze - but it always ends.")]
            public float FinalCollapseDuration = 150f;

            [Tooltip("Damage per second at stage 1, scaling up each stage.")]
            public float MistDamagePerSecond = 2.5f;

            public float MistDamageGrowth = 1.55f;

            [Tooltip("Bots keep the lobby full up to this fraction of the entity count.")]
            public float MaxBotFraction = 1f;

            [Tooltip("Seconds a slot is held for a reconnecting human before a bot takes it.")]
            public float BackfillGrace = 8f;

            public float GliderDeployAltitude = 260f;
            public float GliderSpeed = 42f;

            [Tooltip("Skip the glider descent and start everyone on the ground. Off by default now " +
                     "that the descent sweeps its path instead of teleporting along it; the drop " +
                     "is the opening of a match and a battle royale without one starts nowhere.")]
            public bool SkipInfiltration;
        }

        /// <summary>
        /// The spirit forest: bamboo that grows in from the rampart and squeezes the map.
        ///
        /// A second, physical closing mechanic alongside the mist. The mist punishes you for being
        /// outside a line you can walk back across; the bamboo simply takes the ground away and
        /// will not let you back. It is deliberately silent about damage - it does not need to
        /// hurt, because it cannot be passed.
        /// </summary>
        [Serializable]
        public sealed class BambooSection
        {
            [Tooltip("Grow the spirit forest at all.")]
            public bool Enabled = true;

            [Tooltip("Seconds into the match before the forest appears at all. ZERO by default, " +
                     "and it should stay that way. Any delay here leaves the boundary as an " +
                     "invisible damaging line for that long, which a player can walk straight past " +
                     "with nothing to stop or warn them - the exact problem the bamboo exists to " +
                     "solve. It is kept as a knob only so the mechanic can be staged for testing.")]
            public float FirstGrowth;

            [Tooltip("How far outside the mist line the wall of bamboo stands, in metres.\n\n" +
                     "Zero: the wall IS the boundary. A margin here was a mistake. It left a " +
                     "walkable band between the circle that damages you and the bamboo that stops " +
                     "you, so the boundary still read as an invisible line you could run across " +
                     "with a wall somewhere behind it - which is the whole thing the bamboo " +
                     "exists to fix.\n\n" +
                     "The mist damage is not made redundant by this. It fires on anyone the ring " +
                     "closes over, for as long as it takes the wall to shove them back inside.")]
            public float MistMargin;

            [Tooltip("Seconds the first band takes to reach full height.")]
            public float FirstBandDuration = 60f;

            [Tooltip("Unused since the forest started following the mist. Kept because the " +
                     "growth-schedule test still reads it.")]
            public float BandDuration = 15f;

            [Tooltip("Unused since the forest started following the mist.")]
            public float BandDepth = 1f;

            [Tooltip("Height as a multiple of the rampart's. Tall enough that it cannot be " +
                     "jumped over, and tall enough that landing on top is not a route.")]
            public float HeightMultiple = 2f;

            [Tooltip("How firmly a body caught in the bamboo is pushed back inside, in m/s.")]
            public float PushSpeed = 4f;

            [Tooltip("Damage per second to anyone the forest has closed over.\n\n" +
                     "The push alone was not enough. It runs after the mist and shoves you to just " +
                     "inside the circle, so the mist damage never fired on you either - a player " +
                     "wedged in a room the wall passed through was pushed at, missed, ignored by " +
                     "the mist, and left alive and stuck with no way to die.")]
            public float DamagePerSecond = 14f;

            [Tooltip("Loudness of bamboo being pushed through. It is cover you cannot use quietly.")]
            public float RustleLoudness = 1.5f;

            public float RustleRadius = 26f;

            [Tooltip("Seconds between rustles from one agent, so contact is a sound not a siren.")]
            public float RustleInterval = 0.45f;
        }

        [Serializable]
        public sealed class WaterSection
        {
            [Tooltip("Drown people who stay under. Off makes the river a free hiding place.")]
            public bool Drowning = true;

            [Tooltip("Seconds of air. Nothing happens at all before this.")]
            public float HoldBreathSeconds = 30f;

            [Tooltip("Seconds under before it starts killing you.\n\n" +
                     "The gap between this and HoldBreathSeconds is the design. From thirty " +
                     "seconds the body fights for air and that fight is loud enough to give your " +
                     "position away; from forty-five it is fatal. You are given time to surface, " +
                     "but only by announcing where you are.")]
            public float DrownAfterSeconds = 45f;

            [Tooltip("Damage per second once the air has run out.")]
            public float DrownDamagePerSecond = 11f;

            [Tooltip("Seconds between choking sounds from one drowning body.")]
            public float ChokeInterval = 1.9f;

            [Tooltip("Loudness of choking underwater. Muffled by the water it travels through, " +
                     "but unmistakable from the bank.")]
            public float ChokeLoudness = 0.9f;

            public float ChokeRadius = 30f;
        }

        [Serializable]
        public sealed class ShurikenSection
        {
            public bool Enabled = true;

            [Tooltip("How many everyone starts a match holding. One: it is a decision, not a rate " +
                     "of fire.")]
            public int StartingCount = 1;

            [Tooltip("Most anyone can carry at once, however many they find on the ground.")]
            public int MaxCarried = 3;

            [Tooltip("Seconds between throws. Stops a lucky pickup run becoming a machine gun.")]
            public float Cooldown = 2f;

            [Tooltip("Metres per second. Fast enough to be a threat across a courtyard, slow " +
                     "enough that a moving target is a real miss.")]
            public float Speed = 34f;

            [Tooltip("Downward acceleration in flight. Gentle - a blade is not a thrown rock.")]
            public float Drop = 5.5f;

            [Tooltip("Seconds before a blade that has hit nothing falls out of the air.")]
            public float Lifetime = 2.2f;

            public float Damage = 34f;

            [Tooltip("How near the torso the blade has to pass to count as a hit, in metres.")]
            public float HitRadiusMetres = 0.42f;

            [Tooltip("Seconds a landed blade waits before anyone can pick it up, so a throw is " +
                     "not instantly recovered by the thrower walking forward.")]
            public float PickupDelay = 1.2f;

            public float PickupRadius = 1.7f;

            [Tooltip("Seconds between whistles from one blade. It sounds the whole way, which " +
                     "draws a line from the thrower to wherever it lands.")]
            public float WhistleInterval = 0.16f;

            public float WhistleLoudness = 0.55f;
            public float WhistleRadius = 26f;

            [Tooltip("The throw itself. A body slinging steel is not quiet.")]
            public float ThrowLoudness = 0.6f;

            public float ThrowRadius = 16f;

            public float HitLoudness = 0.9f;
            public float HitRadius = 24f;

            [Tooltip("Visual only: how fast the star turns over in the air.")]
            public float SpinDegreesPerSecond = 1400f;
        }

        [Serializable]
        public sealed class CritterSection
        {
            [Tooltip("Place birds and animals at all.")]
            public bool Enabled = true;

            [Tooltip("Loudness of a bird going up. Louder than a footstep on purpose: it is the " +
                     "price of moving carelessly through a garden.")]
            public float BirdLoudness = 1.15f;

            [Tooltip("How far a flushed bird is heard, in metres.")]
            public float BirdRadius = 44f;

            [Tooltip("Loudness of something small bolting. Quieter and lower than a bird.")]
            public float AnimalLoudness = 0.7f;

            public float AnimalRadius = 26f;
        }

        [Serializable]
        public sealed class BotSection
        {
            [Tooltip("Tick rate for bots inside a combat pocket.")]
            public int CombatTickRate = 30;

            [Tooltip("Tick rate for bots that are alert but not fighting.")]
            public int AlertTickRate = 12;

            [Tooltip("Tick rate for distant, idle bots.")]
            public int IdleTickRate = 4;

            [Tooltip("Range within which a bot is considered near the action.")]
            public float ActiveRange = 60f;

            [Tooltip("How long a bot pursues a lost contact before giving up.")]
            public float InvestigateTimeout = 12f;

            public float SearchTimeout = 18f;

            [Tooltip("Perceived intensity needed to make a bot investigate.")]
            public float NoiseInterestThreshold = 0.18f;

            [Tooltip("Health fraction below which a bot disengages.")]
            public float FleeHealthFraction = 0.3f;

            [Tooltip("Chance per combat decision that a bot attempts a parry.")]
            [Range(0f, 1f)] public float ParryAptitude = 0.55f;

            [Tooltip("Reaction delay before a bot acts on a fresh stimulus.")]
            public float ReactionTime = 0.28f;

            [Tooltip("Random spread applied to bot aim and timing, scaled by difficulty.")]
            [Range(0f, 1f)] public float Sloppiness = 0.35f;
        }

        public NetworkSection Network = new NetworkSection();
        public InterestSection Interest = new InterestSection();
        public StealthSection Stealth = new StealthSection();
        public AudioSection Audio = new AudioSection();
        public MovementSection Movement = new MovementSection();
        public CombatSection Combat = new CombatSection();
        public MatchSection Match = new MatchSection();
        public BambooSection Bamboo = new BambooSection();

        [Tooltip("Birds and small animals, and how loudly they give you away.")]
        public CritterSection Critters = new CritterSection();

        [Tooltip("Being underwater, and running out of air while you are.")]
        public WaterSection Water = new WaterSection();

        [Tooltip("Thrown steel: how far, how hard, and how often.")]
        public ShurikenSection Shuriken = new ShurikenSection();
        public BotSection Bots = new BotSection();

        private static UnseenConfig _default;

        /// <summary>Config used when nothing is assigned in the scene. Loaded from Resources if present.</summary>
        public static UnseenConfig Default
        {
            get
            {
                if (_default != null) return _default;
                _default = Resources.Load<UnseenConfig>("UnseenConfig");
                if (_default == null)
                {
                    _default = CreateInstance<UnseenConfig>();
                    _default.name = "UnseenConfig (code default)";
                }

                return _default;
            }
        }

        public float BaseTickInterval => 1f / Mathf.Max(1, Network.BaseTickRate);
        public float CombatTickInterval => 1f / Mathf.Max(1, Network.CombatTickRate);

        public float StanceHeight(Stance stance)
        {
            switch (stance)
            {
                case Stance.Crouch: return Movement.CrouchHeight;
                case Stance.Prone: return Movement.ProneHeight;
                default: return Movement.StandHeight;
            }
        }

        public float StanceSpeed(Stance stance, bool sprinting)
        {
            switch (stance)
            {
                case Stance.Crouch: return Movement.CrouchSpeed;
                case Stance.Prone: return Movement.ProneSpeed;
                default: return sprinting ? Movement.SprintSpeed : Movement.WalkSpeed;
            }
        }

        public float StanceLoudnessScale(Stance stance, bool sprinting)
        {
            switch (stance)
            {
                case Stance.Crouch: return Audio.CrouchLoudnessScale;
                case Stance.Prone: return Audio.ProneLoudnessScale;
                default: return sprinting ? Audio.SprintLoudnessScale : 1f;
            }
        }
    }
}
