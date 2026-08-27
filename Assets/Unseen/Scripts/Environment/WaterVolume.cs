using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// A body of water you can stand in, and how deep it is where you are standing.
    ///
    /// The river used to be a solid lid: its collider was ordinary world geometry, so a player
    /// walked across the surface with dry feet and the whole channel was a bridge. What makes water
    /// water is that you go into it, which means the surface cannot be a floor - the floor is the
    /// riverbed, and this is what tells the rest of the game how much water is standing on top of
    /// it.
    ///
    /// Queried rather than triggered. A trigger would fire on enter and exit and leave the motor
    /// holding a boolean that can desync from the world after a teleport, a grapple or a respawn;
    /// asking "how deep is it here" from a position is stateless and always right.
    /// </summary>
    public sealed class WaterVolume : MonoBehaviour
    {
        private static readonly List<WaterVolume> Volumes = new List<WaterVolume>(4);

        [Tooltip("World Y of the water surface.")]
        public float SurfaceY;

        [Tooltip("Half-extents on X and Z of the body of water, around this transform.")]
        public Vector2 HalfSize = new Vector2(8f, 200f);

        [Tooltip("Deepest the water gets. Feet below this are treated as standing on the bottom.")]
        public float MaxDepth = 2f;

        [Tooltip("Half-extents of a dry island in the middle of this water, or zero for none. " +
                 "A moat is a body of water with a castle in the middle of it.")]
        public Vector2 InnerHalfSize;

        /// <summary>
        /// Describes a body of water and registers it for queries.
        ///
        /// Registration is explicit rather than left to OnEnable. The generator builds the river in
        /// edit mode for every screenshot, probe and headless test in this project, and Unity does
        /// not run MonoBehaviour lifecycle callbacks in edit mode - so a volume that registered
        /// itself in OnEnable was registered in a real game and invisible to every test of it. The
        /// wading was silently doing nothing in all of them.
        /// </summary>
        public void Configure(float surfaceY, Vector2 halfSize, float maxDepth,
            Vector2 innerHalfSize = default)
        {
            SurfaceY = surfaceY;
            HalfSize = halfSize;
            MaxDepth = maxDepth;
            InnerHalfSize = innerHalfSize;

            if (!Volumes.Contains(this)) Volumes.Add(this);
        }

        /// <summary>
        /// Whether a world column falls in this body of water.
        ///
        /// One place, used by all three queries. They were three copies of the same two
        /// comparisons, which is exactly the shape that lets a moat's dry island exist for wading
        /// and not for drowning.
        /// </summary>
        private bool Covers(float x, float z)
        {
            Vector3 at = transform.position;

            float dx = math.abs(x - at.x);
            float dz = math.abs(z - at.z);

            if (dx > HalfSize.x || dz > HalfSize.y) return false;

            // The island, if there is one. Inside it there is no water at any depth - that is
            // where the keep stands.
            if (InnerHalfSize.x > 0f && InnerHalfSize.y > 0f &&
                dx < InnerHalfSize.x && dz < InnerHalfSize.y)
                return false;

            return true;
        }

        private void OnEnable()
        {
            // Belt and braces for a volume placed in a scene by hand rather than generated.
            if (!Volumes.Contains(this)) Volumes.Add(this);
        }

        private void OnDestroy()
        {
            Volumes.Remove(this);
        }

        /// <summary>How many bodies of water are registered. Diagnostics only.</summary>
        public static int Registered => Volumes.Count;

        /// <summary>
        /// Metres of water standing above a pair of feet. Zero on dry land.
        ///
        /// The DEEPEST answer rather than the first one. There is more than one body of water in
        /// this town now - a river and a castle moat - and if two ever overlap, which one a player
        /// is standing in should not depend on which was generated first.
        /// </summary>
        public static float DepthAt(float3 feet)
        {
            float deepest = 0f;

            for (int i = 0; i < Volumes.Count; i++)
            {
                WaterVolume volume = Volumes[i];
                if (volume == null) continue;

                if (!volume.Covers(feet.x, feet.z)) continue;

                float over = volume.SurfaceY - feet.y;
                if (over <= 0.02f) continue;

                deepest = math.max(deepest, math.min(over, volume.MaxDepth));
            }

            return deepest;
        }

        /// <summary>
        /// True if a world point is beneath the surface of some body of water.
        ///
        /// Takes the point directly rather than a pair of feet and a height, because the thing the
        /// drowning clock cares about is one specific point - the eye - and reconstructing it from
        /// feet plus a stance-dependent offset in two places is how the two drift apart.
        /// </summary>
        public static bool IsUnder(float3 point)
        {
            for (int i = 0; i < Volumes.Count; i++)
            {
                WaterVolume volume = Volumes[i];
                if (volume == null) continue;

                if (!volume.Covers(point.x, point.z)) continue;
                if (point.y < volume.SurfaceY) return true;
            }

            return false;
        }

        /// <summary>True if the given eye height is under the surface.</summary>
        public static bool IsSubmerged(float3 feet, float eyeHeight)
        {
            for (int i = 0; i < Volumes.Count; i++)
            {
                WaterVolume volume = Volumes[i];
                if (volume == null) continue;

                if (!volume.Covers(feet.x, feet.z)) continue;

                // Any water over the eye counts, so a shallow puddle overlapping a deep channel
                // cannot report a head above water that is actually under one.
                if (feet.y + eyeHeight < volume.SurfaceY) return true;
            }

            return false;
        }
    }
}
