using UnityEditor;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Whether the Blender sculpt is actually on the ninja.
    ///
    /// Worth a test of its own because every way this fails looks like success. The refinement is
    /// applied to the imported body mesh rather than replacing it, so a missing export, an
    /// unreadable source or a changed vertex count all end with an agent that renders perfectly
    /// well in the unsculpted body - and nothing anywhere says so.
    ///
    /// It has already shipped broken once exactly this way. `characterMedium.fbx` was imported
    /// without Read/Write, which the editor papers over by keeping mesh data readable, so the
    /// sculpt worked on this machine and was silently dropped in every player build. It was caught
    /// by looking hard at a screenshot, which is not a test.
    /// </summary>
    public static class UnseenNinjaArtTest
    {
        [MenuItem("Unseen/Test Ninja Art", priority = 94)]
        public static void Run()
        {
            var host = new GameObject("NinjaArtTest");
            bool passed = true;

            try
            {
                UnseenBootstrap boot = host.AddComponent<UnseenBootstrap>();
                boot.Mode = LaunchMode.ListenServer;
                boot.GenerateGreyboxIfEmpty = true;
                boot.StatusLogInterval = 0f;
                boot.VerboseStartup = false;
                boot.BuildNavMesh = false;
                boot.Seed = 20260827;
                boot.Boot();

                Step(boot, 120);

                AgentEntity agent = null;
                foreach (AgentEntity a in boot.Context.Entities.All)
                    if (a != null) { agent = a; break; }

                if (agent == null)
                {
                    Debug.LogError("[ninja-art] no agents spawned");
                    return;
                }

                var visual = agent.GetComponentInChildren<AgentVisual>();

                passed &= Check("an agent has a visual at all", visual != null);
                if (visual == null) { Debug.Log("[ninja-art] FAILED"); return; }

                passed &= Check("with a body mesh", visual.Body != null && visual.Body.sharedMesh != null);
                if (visual.Body == null || visual.Body.sharedMesh == null)
                {
                    Debug.Log("[ninja-art] FAILED");
                    return;
                }

                Mesh mesh = visual.Body.sharedMesh;
                Debug.Log($"[ninja-art] body mesh is '{mesh.name}', {mesh.vertexCount} vertices");

                // Two generations of ninja, and either is a pass.
                //
                // The Hero mesh supersedes the older path entirely: NinjaBodyArt returns early when
                // HeroNinjaAppearance.IsHero is true, because the sculpt it applies is a refinement
                // of characterMedium and the Hero body is a different mesh altogether. Asserting
                // the old path was the first version of this test, and it failed against a project
                // that was working perfectly - it was measuring the wrong generation.
                //
                // What actually has to be true is narrower and survives both: the agent is not
                // wearing the bare import. That is the failure that shipped once and the one worth
                // catching.
                bool hero = mesh.name.StartsWith("HeroNinja");
                bool refined = mesh.name.EndsWith("_Refined");

                passed &= Check($"the body is a finished ninja, not the raw import " +
                                $"({(hero ? "Hero" : refined ? "Blender refinement" : "RAW IMPORT")})",
                    hero || refined);

                // Only meaningful on the older path. With the Hero mesh in place the refinement is
                // supposed to do nothing, and demanding it had run would be demanding the wrong
                // thing.
                if (!hero)
                {
                    passed &= Check("and the pipeline says it applied it", NinjaBodyArt.Applied);

                    // The source has to stay readable or the refinement cannot be applied in a
                    // player build - the failure that already happened once and is invisible here,
                    // because the editor keeps mesh data readable whatever the import says.
                    passed &= Check("the source mesh is readable, so a player build can too",
                        SourceIsReadable());
                }

                Debug.Log(passed ? "[ninja-art] PASSED" : "[ninja-art] FAILED");
            }
            finally
            {
                Object.DestroyImmediate(host);

                foreach (MapDescriptor map in Object.FindObjectsByType<MapDescriptor>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (map != null) Object.DestroyImmediate(map.gameObject);

                GameObject town = GameObject.Find("GreyboxTown");
                if (town != null) Object.DestroyImmediate(town);
            }
        }

        /// <summary>
        /// Asks the importer, not the loaded mesh.
        ///
        /// The editor keeps mesh data readable whatever the import settings say, so checking
        /// `mesh.isReadable` at runtime here would answer yes even for the import that broke every
        /// player build. The import setting is the thing a build actually obeys.
        /// </summary>
        private static bool SourceIsReadable()
        {
            const string path = "Assets/Unseen/Art/Characters/characterMedium.fbx";

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;

            if (importer == null)
            {
                Debug.LogError($"[ninja-art] no model importer at {path}");
                return false;
            }

            return importer.isReadable;
        }

        private static bool Check(string what, bool ok)
        {
            Debug.Log($"[ninja-art] {what}: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        private static void Step(UnseenBootstrap boot, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                boot.Network.Poll(1f / 60f);
                boot.Simulation.Advance(1f / 60f);
            }
        }
    }
}
