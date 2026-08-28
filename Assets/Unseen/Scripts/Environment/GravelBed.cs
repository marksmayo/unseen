using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// A bed of raked gravel that records who has walked across it.
    ///
    /// The karesansui was already the most dangerous ground in the town by design - a walled
    /// rectangle of open gravel with a clear sightline across it, which is why nobody sensible
    /// crosses one. This makes it worse, and makes it interesting: it remembers.
    ///
    /// A print is not a decoration. Somebody who crosses a garden leaves a line of them pointing
    /// exactly the way they went, and anybody arriving afterwards can read it - which turns the
    /// garden from a place you avoid into a place you check.
    ///
    /// Registered explicitly rather than in OnEnable. The town is generated in edit mode for every
    /// screenshot and probe in this project and Unity runs no lifecycle callbacks there, so a bed
    /// that registered itself in OnEnable would exist in a real game and be invisible to every test
    /// of it - a mistake this project has already made twice, with water volumes and with critters.
    /// </summary>
    public sealed class GravelBed : MonoBehaviour
    {
        private static readonly List<GravelBed> Beds = new List<GravelBed>(8);

        [Tooltip("Half-extents on X and Z of the raked surface, around this transform.")]
        public Vector2 HalfSize = new Vector2(12f, 12f);

        [Tooltip("World Y of the gravel surface: where a print sits.")]
        public float SurfaceY;

        /// <summary>Every raked bed in the level. Read by the footprint system and the probes.</summary>
        public static IReadOnlyList<GravelBed> All => Beds;

        public void Configure(Vector2 halfSize, float surfaceY)
        {
            HalfSize = halfSize;
            SurfaceY = surfaceY;

            if (!Beds.Contains(this)) Beds.Add(this);
        }

        private void OnEnable()
        {
            // Belt and braces for a bed placed in a scene by hand rather than generated.
            if (!Beds.Contains(this)) Beds.Add(this);
        }

        private void OnDestroy() => Beds.Remove(this);

        /// <summary>
        /// The bed a pair of feet is standing on, or null.
        ///
        /// Height is checked as well as footprint: the gardens have walls around them and a player
        /// on top of one is over the gravel without being on it.
        /// </summary>
        public static GravelBed At(float3 feet)
        {
            for (int i = 0; i < Beds.Count; i++)
            {
                GravelBed bed = Beds[i];
                if (bed == null) continue;

                Vector3 at = bed.transform.position;

                if (math.abs(feet.x - at.x) > bed.HalfSize.x) continue;
                if (math.abs(feet.z - at.z) > bed.HalfSize.y) continue;

                // Within a stride's worth of the surface, up or down.
                if (math.abs(feet.y - bed.SurfaceY) > 0.6f) continue;

                return bed;
            }

            return null;
        }
    }
}
