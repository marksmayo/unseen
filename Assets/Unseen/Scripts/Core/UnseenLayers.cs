using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// Layer indices declared in ProjectSettings/TagManager.asset.
    /// UnseenProjectSetup (editor) validates and repairs these on demand.
    /// </summary>
    public static class UnseenLayers
    {
        public const int Default = 0;
        public const int Occluder = 8;
        public const int ShojiPaper = 9;
        public const int Ninja = 10;
        public const int Interactable = 11;
        public const int Rafter = 12;
        public const int Climbable = 13;
        public const int GrappleAnchor = 14;
        public const int MistVolume = 15;
        public const int LootContainer = 16;
        public const int Foliage = 17;

        /// <summary>
        /// Renderer-only trim: rafter ends, eave sweeps, balusters, ridge caps, rake lines.
        ///
        /// Nothing on this layer has a collider, so it is invisible to every raycast in the game
        /// and the layer exists purely so the camera can stop drawing it at a distance. It is a
        /// little over half of everything in the town by renderer count and about a quarter of a
        /// triangle each, which is exactly the profile that should not be drawn from four hundred
        /// metres away.
        /// </summary>
        public const int Decoration = 18;

        /// <summary>
        /// The mist panels lying in the streets. Renderer-only, like Decoration, and on their own
        /// layer for the same reason: so the camera can stop drawing them at a distance.
        ///
        /// It earns a layer of its own rather than sharing Decoration because the two want very
        /// different distances - trim is sub-pixel at eighty-five metres, whereas a bank of fog is
        /// still a bank of fog, so the mist has to survive further out.
        ///
        /// Measured, not guessed at: six hundred and thirty of these quads cover a hundred and
        /// seventy-four thousand square metres of a five hundred and sixty-two thousand square
        /// metre map. That is a third of the world in alpha, and transparent area is paid for every
        /// frame whether or not anything is behind it. They were the single largest rendering cost
        /// in the town and nothing was culling them, because they were left on Default.
        /// </summary>
        public const int GroundMist = 19;

        public static readonly string[] CustomLayerNames =
        {
            "Occluder", "ShojiPaper", "Ninja", "Interactable", "Rafter",
            "Climbable", "GrappleAnchor", "MistVolume", "LootContainer", "Foliage",
            "Decoration", "GroundMist"
        };

        /// <summary>Geometry that fully breaks line of sight.</summary>
        public static LayerMask SightBlockers =>
            (1 << Default) | (1 << Occluder) | (1 << ShojiPaper);

        /// <summary>Geometry that attenuates sound, weighted by AcousticMaterial.</summary>
        public static LayerMask SoundBlockers =>
            (1 << Default) | (1 << Occluder) | (1 << ShojiPaper) | (1 << Rafter);

        /// <summary>Geometry that casts a shadow for the stealth index.</summary>
        public static LayerMask LightBlockers =>
            (1 << Default) | (1 << Occluder) | (1 << ShojiPaper) | (1 << Rafter) | (1 << Foliage);

        /// <summary>Solid world used by the parkour probes.</summary>
        public static LayerMask WorldGeometry =>
            (1 << Default) | (1 << Occluder) | (1 << Climbable) | (1 << Rafter);

        public static LayerMask Agents => 1 << Ninja;

        public static LayerMask Climb => (1 << Climbable) | (1 << Occluder) | (1 << Default);

        public static LayerMask Grapple => 1 << GrappleAnchor;

        /// <summary>
        /// Applies the collision matrix the gameplay layers assume. This is runtime state rather
        /// than a project setting so it is applied on boot, and re-applied identically on a server.
        /// </summary>
        public static void ApplyCollisionMatrix()
        {
            // Interest volumes and grapple anchors are query-only: nothing physically bumps them.
            for (int other = 0; other < 32; other++)
            {
                Physics.IgnoreLayerCollision(MistVolume, other, true);
                Physics.IgnoreLayerCollision(GrappleAnchor, other, true);
            }

            // A chest is a crate on the floor: you walk round it, not through it. It used to be
            // non-solid on the theory that props are pickups rather than obstacles, which reads as
            // a collision bug the first time you stroll through a strongbox.
            //
            // Interactables (hung lanterns, noren) and foliage stay pass-through on purpose:
            // brushing a paper lamp or a bush should not stop a sprint.
            Physics.IgnoreLayerCollision(Ninja, Interactable, true);
            Physics.IgnoreLayerCollision(Ninja, Foliage, true);
        }
    }
}
