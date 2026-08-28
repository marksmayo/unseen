using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Checks that a denser, more irregular town is still a town you can walk through.
    ///
    /// Narrow streets, block jitter and block rotation all pull in the same direction: they make
    /// the place look like it grew rather than being set out. They also all push toward the same
    /// failure, which is two neighbours leaning together until the alley between them is sealed.
    /// A sealed alley is invisible in a screenshot and fatal in play - it strands bots, breaks
    /// routes, and turns a shortcut into a dead end.
    ///
    /// So this measures the street space itself rather than the buildings: it floods the walkable
    /// ground from one point and reports how much of the town that flood can reach. Everything the
    /// flood cannot reach is somewhere a player can see and never get to.
    /// </summary>
    public static class UnseenStreetProbe
    {
        /// <summary>Half the width of the corridor a body needs. Generous - a bot is not a point.</summary>
        private const float AgentRadius = 0.45f;

        /// <summary>Sample spacing, in metres. Fine enough to find a pinch, coarse enough to run.</summary>
        private const float Cell = 1.5f;

        /// <summary>Above this is roof, which is meant to be cut off from the street.</summary>
        private const float StreetCeiling = 4f;

        [MenuItem("Unseen/Probe Streets", priority = 68)]
        public static void Run()
        {
            var host = new GameObject("StreetProbe");

            try
            {
                var gen = host.AddComponent<GreyboxTownGenerator>();
                gen.Seed = 20260828;
                gen.Generate();

                // Edit mode does not sync transforms, so a collider that was created and then
                // positioned is still sitting at the origin as far as the physics scene is
                // concerned. Every overlap query below would come back empty and the town would
                // look perfectly passable. This has caught me out twice.
                Physics.SyncTransforms();

                float extent = Extent(gen);
                int side = Mathf.CeilToInt(extent * 2f / Cell);

                Debug.Log($"[streets] {side}x{side} samples over {extent * 2f:0} m of town " +
                          $"(block {gen.BlockSize:0.0} m, street {gen.StreetWidth:0.0} m, " +
                          $"jitter {gen.BlockJitter:0.0} m, turn {gen.BlockRotation:0.0} deg)");

                // ------------------------------------------------------------------ what is open
                //
                // Sampled on the ground the sample actually finds, not at a fixed height. The
                // first version tested a capsule at y = 0.1 everywhere and reported the current
                // town - the one people are playing right now - as 2.2% connected, because the
                // castle sits on a plinth and the probe had buried its own capsule inside it.
                var open = new bool[side, side];
                int openCount = 0;

                for (int x = 0; x < side; x++)
                for (int z = 0; z < side; z++)
                {
                    Vector3 from = Point(x, z, side, extent);

                    if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 200f)) continue;

                    // Roofs are open ground you can stand on and are deliberately not connected to
                    // the street - reaching them is what the grapple is for. Counting them would
                    // score a working town as broken. Walls start at WallHeight, so anything below
                    // four metres is street, towpath, bridge or courtyard.
                    if (hit.point.y > StreetCeiling) continue;

                    // A body standing here: a capsule from knee to head, not a point at the
                    // ankles. A point sample walks straight through a handrail.
                    if (Physics.CheckCapsule(hit.point + Vector3.up * 0.5f,
                            hit.point + Vector3.up * 1.7f, AgentRadius)) continue;

                    open[x, z] = true;
                    openCount++;
                }

                // ------------------------------------------------------------------ what is reachable
                //
                // The largest connected component rather than a flood from a chosen start. Any
                // start point is a guess about where the streets are, and a guess that lands in a
                // courtyard reports the whole town broken. Asking instead "how much of the
                // walkable ground is in one piece" needs no guess and is the property I actually
                // want to hold.
                int reached = LargestComponent(open, side);
                float coverage = openCount == 0 ? 0f : reached / (float)openCount;

                Debug.Log($"[streets] {openCount} walkable samples, largest connected piece holds " +
                          $"{reached}: {coverage * 100f:0.0}%");

                // Ninety-five per cent, not a hundred. Courtyards inside compounds are meant to be
                // sealed from the street - that is what a wall with one gate is for - so a town
                // with no unreachable ground at all would mean the walls were not working.
                bool connected = coverage >= 0.95f;
                Debug.Log($"[streets] the streets all join up: {(connected ? "PASS" : "FAIL")}");

                // ------------------------------------------------------------------ and it is dense
                //
                // The counterfactual for the connectivity test. A town of nothing but open ground
                // would score a hundred per cent connected and would not be a town.
                float builtFraction = 1f - openCount / (float)(side * side);

                Debug.Log($"[streets] {builtFraction * 100f:0.0}% of the ground is built on or " +
                          $"blocked");

                bool dense = builtFraction > 0.25f;
                Debug.Log($"[streets] the town is actually dense: {(dense ? "PASS" : "FAIL")}");

                if (connected && dense) Debug.Log("[streets] PASSED");
                else Debug.LogError("[streets] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static Vector3 Point(int x, int z, int side, float extent) =>
            new Vector3(-extent + x * Cell, 80f, -extent + z * Cell);

        private static float Extent(GreyboxTownGenerator gen) =>
            (gen.BlockSize + gen.StreetWidth) * gen.GridSize * 0.5f;

        /// <summary>The biggest single piece of connected walkable ground.</summary>
        private static int LargestComponent(bool[,] open, int side)
        {
            var seen = new bool[side, side];
            int biggest = 0;

            for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
            {
                if (!open[x, z] || seen[x, z]) continue;
                biggest = Mathf.Max(biggest, Flood(open, seen, side, x, z));
            }

            return biggest;
        }

        /// <summary>Four-way flood fill. Returns how many open samples the start can reach.</summary>
        private static int Flood(bool[,] open, bool[,] seen, int side, int startX, int startZ)
        {
            var queue = new Queue<Vector2Int>();

            queue.Enqueue(new Vector2Int(startX, startZ));
            seen[startX, startZ] = true;
            int count = 0;

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                Vector2Int at = queue.Dequeue();
                count++;

                for (int d = 0; d < 4; d++)
                {
                    int nx = at.x + dx[d], nz = at.y + dz[d];
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    if (seen[nx, nz] || !open[nx, nz]) continue;

                    seen[nx, nz] = true;
                    queue.Enqueue(new Vector2Int(nx, nz));
                }
            }

            return count;
        }
    }
}
