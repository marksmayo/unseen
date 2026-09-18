using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Whether a server and a client agree on which shoji is which.
    ///
    /// They exchange no list. Both sides build the same town from the same seed, sort every
    /// destructible by quantised position, and number them - so id 47 means the same panel in both
    /// processes because both processes counted in the same order. Elegant, cheap on the wire, and
    /// entirely unproven: it has never once been checked that two independent builds of the same
    /// town produce the same numbering.
    ///
    /// The cost of being wrong is specific and nasty. A world event carries an id, so a mismatch
    /// means the server says "panel 47 is broken" and the client breaks a different one somewhere
    /// else in the town. Nobody would read that as a networking fault: it looks like walls falling
    /// apart on their own, and the player would see through a screen the server still thinks is
    /// solid - which in a stealth game is a free look at somebody who believes they are hidden.
    ///
    /// Two things are checked, and the second matters more than the first. That one run agrees with
    /// another is reassuring; that no two destructibles share a quantised position is what makes it
    /// reliable, because the sort's tie-break is arbitrary and unstable.
    /// </summary>
    public static class UnseenDestructibleIdTest
    {
        [MenuItem("Unseen/Test Destructible Ids", priority = 96)]
        public static void Run()
        {
            bool passed = true;

            List<string> first = Catalogue(20260827);
            List<string> second = Catalogue(20260827);
            List<string> different = Catalogue(20260828);

            passed &= Check("the same seed builds the same number of destructibles",
                first.Count == second.Count);

            if (first.Count == second.Count)
            {
                int mismatched = 0;
                for (int i = 0; i < first.Count; i++)
                    if (first[i] != second[i]) mismatched++;

                passed &= Check($"every id names the same object in both builds ({mismatched} differ)",
                    mismatched == 0);
            }

            // The control. If two different seeds produced identical catalogues, the check above
            // would be passing because it is comparing nothing.
            passed &= Check("and a different seed really does build a different town",
                different.Count != first.Count || !SameAs(first, different));

            passed &= CheckTies(20260827);
            passed &= CheckTies(20260901);
            passed &= CheckTies(1);

            Debug.Log(passed ? "[destructibles] PASSED" : "[destructibles] FAILED");
        }

        private static bool SameAs(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Every destructible in id order, described by where it is.
        ///
        /// Described by position rather than compared by reference, because the two builds are
        /// different objects in different scenes. What has to match is the association between an
        /// id and a place in the world, since that is all an id means.
        /// </summary>
        private static List<string> Catalogue(int seed)
        {
            var host = new GameObject($"DestructibleIdTest-{seed}");
            var lines = new List<string>();

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.BuildNavMesh = false;
                boot.Seed = seed;
                boot.Boot();

                DestructibleRegistry registry = boot.Context.Destructibles;

                for (ushort id = 0; ; id++)
                {
                    ShojiPanel panel = registry.PanelById(id);
                    if (panel == null) break;
                    lines.Add($"panel {id} {Describe(panel.Position)}");
                }

                for (ushort id = 0; ; id++)
                {
                    Lantern lantern = registry.LanternById(id);
                    if (lantern == null) break;
                    lines.Add($"lantern {id} {Describe(lantern.Position)}");
                }

                for (ushort id = 0; ; id++)
                {
                    Items.LootContainer container = registry.ContainerById(id);
                    if (container == null) break;
                    lines.Add($"container {id} {Describe(container.Position)}");
                }

                Debug.Log($"[destructibles] seed {seed}: {lines.Count} destructibles indexed");
            }
            finally
            {
                TearDown(host);
            }

            return lines;
        }

        /// <summary>
        /// Looks for two destructibles of a kind at the same quantised position.
        ///
        /// This is the real test. Ids come from sorting on position quantised to the centimetre,
        /// and the comparison returns "equal" for a tie - at which point the order is whatever the
        /// sort happened to leave, and the sort is not a stable one. Two processes could then
        /// number the same town differently with no bug in either, and the failure would appear as
        /// walls breaking in the wrong place on somebody else's screen.
        /// </summary>
        private static bool CheckTies(int seed)
        {
            var host = new GameObject($"DestructibleTieTest-{seed}");

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.BuildNavMesh = false;
                boot.Seed = seed;
                boot.Boot();

                DestructibleRegistry registry = boot.Context.Destructibles;

                int panels = TiesAmong(Poses(id =>
                {
                    ShojiPanel p = registry.PanelById(id);
                    return p == null ? null : Pose(p.Position, p.transform.forward);
                }));

                int lanterns = TiesAmong(Poses(id =>
                {
                    Lantern l = registry.LanternById(id);
                    return l == null ? null : Pose(l.Position, l.transform.forward);
                }));

                int containers = TiesAmong(Poses(id =>
                {
                    Items.LootContainer c = registry.ContainerById(id);
                    return c == null ? null : Pose(c.Position, c.transform.forward);
                }));
                int ties = panels + lanterns + containers;

                return Check($"seed {seed}: no two destructibles share a quantised position " +
                             $"({ties} collisions: {panels} panels, {lanterns} lanterns, " +
                             $"{containers} containers)", ties == 0);
            }
            finally
            {
                TearDown(host);
            }
        }

        /// <summary>
        /// Takes the whole world away, not just the bootstrap.
        ///
        /// The generated town is its own root object - ResolveMap creates "GreyboxTown" beside the
        /// bootstrap rather than under it - so destroying the host alone leaves the town standing,
        /// and the next boot finds it and reuses it instead of generating its own.
        ///
        /// That is not a detail. The first version of this probe did exactly that and reported that
        /// two builds agreed perfectly on every id, which was true and meant nothing: they were the
        /// same objects. It also reported that two different seeds produced identical towns, which
        /// is what gave the game away.
        /// </summary>
        private static void TearDown(GameObject host)
        {
            Object.DestroyImmediate(host);

            foreach (MapDescriptor map in Object.FindObjectsByType<MapDescriptor>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (map != null) Object.DestroyImmediate(map.gameObject);
            }

            GameObject town = GameObject.Find("GreyboxTown");
            if (town != null) Object.DestroyImmediate(town);
        }

        /// <summary>
        /// Every destructible's sort key, in id order. Must describe the same thing the registry
        /// sorts on, or this measures a collision the registry does not have.
        /// </summary>
        private static List<string> Poses(System.Func<ushort, string> at)
        {
            var found = new List<string>();

            for (ushort id = 0; ; id++)
            {
                string pose = at(id);
                if (pose == null) break;
                found.Add(pose);
            }

            return found;
        }

        private static string Pose(float3 position, Vector3 forward) =>
            $"{Describe(position)}|{Mathf.RoundToInt(forward.x * 64f)}," +
            $"{Mathf.RoundToInt(forward.y * 64f)},{Mathf.RoundToInt(forward.z * 64f)}";

        private static int TiesAmong(List<string> poses)
        {
            var seen = new HashSet<string>();
            int ties = 0;

            for (int i = 0; i < poses.Count; i++)
                if (!seen.Add(poses[i])) ties++;

            return ties;
        }

        private static string Describe(float3 p) =>
            $"{Mathf.RoundToInt(p.x * 100f)},{Mathf.RoundToInt(p.y * 100f)},{Mathf.RoundToInt(p.z * 100f)}";

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[destructibles] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }
    }
}
