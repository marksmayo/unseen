using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.AI
{
    /// <summary>
    /// Builds a NavMesh over the generated town, at runtime, once the town exists.
    ///
    /// There has never been one. <see cref="BotNavigator"/> has been fully written to use a NavMesh
    /// since it was first committed - it samples, it calculates a path, it follows corners - and
    /// every one of those calls has silently failed and dropped through to whisker steering,
    /// because nothing was ever baked. That was not an oversight anybody could fix in the editor:
    /// the town is generated procedurally at startup and differs by seed, so there is no level to
    /// bake in advance. It has to be built after generation, in the process, every time.
    ///
    /// Two decisions matter here.
    ///
    /// **Colliders, not render meshes.** The town is roughly ninety-five thousand renderers and
    /// fifteen thousand colliders, and the difference is decoration: mist panels, eave sweeps,
    /// foliage, rake lines. Voxelising all of that would be slow and would also produce a NavMesh
    /// with holes in it wherever a renderer-only bush stood.
    ///
    /// **Deep water is not walkable.** Bots have no notion of drowning - nothing in the HTN domain
    /// models breath - so a lake at the middle of the map is a death trap that quietly thins the
    /// roster. Marking the deep parts unwalkable sends them over the bridges, which is both better
    /// behaviour and better play: the bridges are the obvious place to be watched from. The river's
    /// shallow shelves stay walkable, so a bot can still ford it at the edges. Players are
    /// unaffected - this changes nothing about where a human can wade.
    /// </summary>
    public static class NavMeshBaker
    {
        /// <summary>Unity's built-in area indices. 1 is Not Walkable and is not configurable.</summary>
        private const int NotWalkableArea = 1;

        /// <summary>
        /// Voxel size for the bake, in metres.
        ///
        /// Coarser than the default, which is a third of the agent radius - about 17 cm. The town
        /// is seven hundred and fifty metres across, and at 17 cm that is four and a half thousand
        /// columns per axis to solve at match start. Bots navigate streets and rooftops, not
        /// centimetres, and this is the single number that decides whether the bake costs seconds
        /// or minutes.
        /// </summary>
        public static float VoxelSize = 0.32f;

        /// <summary>How far above the tallest thing in the town to start voxelising.</summary>
        public static float Headroom = 12f;

        /// <summary>Seconds the last bake took. Reported by the probes.</summary>
        public static float LastBakeSeconds { get; private set; }

        /// <summary>Water volumes carved out as unwalkable by the last bake.</summary>
        public static int LastCarvedVolumes { get; private set; }

        /// <summary>
        /// Builds a NavMesh covering the map. Returns false if there was nothing to build over.
        /// </summary>
        public static bool Build(MapDescriptor map)
        {
            if (map == null) return false;

            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            var host = new GameObject("NavMesh");
            host.transform.position = map.Center;

            LastCarvedVolumes = CarveWater(host.transform);

            // The volume to voxelise. Horizontally the playable box, and vertically from below the
            // sewers to well above the keep - rooftops are a legitimate route and a bake that
            // stopped at the eaves would quietly make them unreachable.
            float half = map.HalfExtent > 0f ? map.HalfExtent : map.Radius;
            float floor = map.FloorY - 4f;
            float ceiling = Mathf.Max(map.CeilingY, 40f) + Headroom;

            var surface = host.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(0f, (floor + ceiling) * 0.5f - map.Center.y, 0f);
            surface.size = new Vector3(half * 2f + 8f, ceiling - floor, half * 2f + 8f);

            // Physics colliders only. See the note on the class.
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = UnseenLayers.WorldGeometry;

            surface.overrideVoxelSize = true;
            surface.voxelSize = VoxelSize;

            surface.overrideTileSize = true;
            surface.tileSize = 256;

            surface.BuildNavMesh();

            LastBakeSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start) /
                              (float)System.Diagnostics.Stopwatch.Frequency;

            return true;
        }

        /// <summary>
        /// Marks the drownable part of every body of water unwalkable.
        ///
        /// Placed from the registered <see cref="WaterVolume"/>s rather than from the generator's
        /// own numbers, so this stays correct for any water anybody adds later without the bake
        /// needing to know what a river or a lake is.
        /// </summary>
        private static int CarveWater(Transform parent)
        {
            int carved = 0;

            for (int i = 0; i < WaterVolume.All.Count; i++)
            {
                WaterVolume water = WaterVolume.All[i];
                if (water == null) continue;

                Vector2 deep = water.DeepFootprint;
                if (deep.x <= 0f || deep.y <= 0f) continue;

                Vector3 at = water.transform.position;

                // From below the bed to just above the surface.
                //
                // The top has to clear the waterline or a bot can stand on the last walkable voxel
                // with its head under. The bottom has to clear the bed, or the bed's own surface
                // survives the carve and the whole exercise does nothing.
                float top = water.SurfaceY + 0.5f;
                float bottom = water.SurfaceY - water.MaxDepth - 2.5f;

                var volume = new GameObject($"NoWade_{i}");
                volume.transform.SetParent(parent, false);
                volume.transform.position = new Vector3(at.x, (top + bottom) * 0.5f, at.z);

                var modifier = volume.AddComponent<NavMeshModifierVolume>();
                modifier.size = new Vector3(deep.x * 2f, top - bottom, deep.y * 2f);
                modifier.center = Vector3.zero;
                modifier.area = NotWalkableArea;

                carved++;

                // An island inside the water - the castle stands on one - needs nothing done to it.
                // Its walkable surface is the top of a plinth well above the waterline, so the carve
                // volume passes through solid stone underneath it and never touches the floor
                // anybody walks on.
            }

            return carved;
        }
    }
}
