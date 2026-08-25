using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unseen.Core;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// One-shot project setup. Everything here is idempotent, so running it on an existing project
    /// repairs drift rather than clobbering work.
    /// </summary>
    public static class UnseenProjectSetup
    {
        private const string ConfigPath = "Assets/Unseen/Resources/UnseenConfig.asset";
        private const string ScenePath = "Assets/Unseen/Scenes/Unseen_Game.unity";

        [MenuItem("Unseen/Setup/Run All Setup Steps", priority = 0)]
        public static void RunAll()
        {
            ValidateLayers();
            CreateConfigAsset();
            ConfigurePhysics();
            CreateGameScene();
            Debug.Log("[Unseen] project setup complete.");
        }

        [MenuItem("Unseen/Setup/Validate Layers", priority = 20)]
        public static void ValidateLayers()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (asset == null || asset.Length == 0)
            {
                Debug.LogWarning("[Unseen] could not open TagManager.asset to validate layers.");
                return;
            }

            var tagManager = new SerializedObject(asset[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            if (layers == null)
            {
                Debug.LogWarning("[Unseen] TagManager.asset has no layers array.");
                return;
            }

            var missing = new List<string>();
            for (int i = 0; i < UnseenLayers.CustomLayerNames.Length; i++)
            {
                string expected = UnseenLayers.CustomLayerNames[i];
                int index = 8 + i;
                if (index >= layers.arraySize) break;

                SerializedProperty slot = layers.GetArrayElementAtIndex(index);
                if (slot.stringValue == expected) continue;

                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = expected;
                    continue;
                }

                missing.Add($"layer {index} is '{slot.stringValue}', expected '{expected}'");
            }

            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            if (missing.Count == 0)
            {
                Debug.Log("[Unseen] layers validated.");
                return;
            }

            Debug.LogError("[Unseen] layer conflicts found - fix these by hand, the gameplay masks " +
                           "depend on the indices:\n  " + string.Join("\n  ", missing));
        }

        [MenuItem("Unseen/Setup/Create Config Asset", priority = 21)]
        public static void CreateConfigAsset()
        {
            if (File.Exists(ConfigPath))
            {
                Debug.Log($"[Unseen] config already exists at {ConfigPath}.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            UnseenConfig config = ScriptableObject.CreateInstance<UnseenConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Unseen] created {ConfigPath}. It is loaded automatically from Resources.");
        }

        [MenuItem("Unseen/Setup/Configure Physics Matrix", priority = 22)]
        public static void ConfigurePhysics()
        {
            // Note: this is runtime state, not a serialised project setting. The same call runs on
            // boot from UnseenBootstrap, so a build and a dedicated server get the same matrix.
            UnseenLayers.ApplyCollisionMatrix();
            Debug.Log("[Unseen] physics layer collisions applied for this session. " +
                      "UnseenBootstrap re-applies them at runtime.");
        }

        [MenuItem("Unseen/Setup/Create Game Scene", priority = 23)]
        public static void CreateGameScene()
        {
            if (File.Exists(ScenePath))
            {
                Debug.Log($"[Unseen] scene already exists at {ScenePath}.");
                AddSceneToBuildSettings();
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var bootstrapHost = new GameObject("UnseenBootstrap");
            UnseenBootstrap bootstrap = bootstrapHost.AddComponent<UnseenBootstrap>();
            bootstrap.Mode = LaunchMode.OfflinePractice;
            bootstrap.GenerateGreyboxIfEmpty = true;

            UnseenConfig config = AssetDatabase.LoadAssetAtPath<UnseenConfig>(ConfigPath);
            if (config != null) bootstrap.Config = config;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings();
            Debug.Log($"[Unseen] created {ScenePath}. Press play - the greybox town builds itself.");
        }

        private static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
                if (scenes[i].path == ScenePath)
                    return;

            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        [MenuItem("Unseen/Open Game Scene", priority = 30)]
        public static void OpenGameScene()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogWarning($"[Unseen] {ScenePath} does not exist yet - run Unseen/Setup first.");
                return;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[Unseen] opened {ScenePath}. Press Play.");
        }

        [MenuItem("Unseen/Generate Greybox Town In Open Scene", priority = 40)]
        public static void GenerateGreybox()
        {
            var host = new GameObject("GreyboxTown");
            Undo.RegisterCreatedObjectUndo(host, "Generate Greybox Town");

            GreyboxTownGenerator generator = host.AddComponent<GreyboxTownGenerator>();
            MapDescriptor map = generator.Generate();

            Selection.activeGameObject = host;
            EditorSceneManager.MarkSceneDirty(host.scene);
            Debug.Log($"[Unseen] greybox town generated, radius {map.Radius:0} m. " +
                      "Bake a NavMesh over it for sharper bot pathing.");
        }
    }
}
