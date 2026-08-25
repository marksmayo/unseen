using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Opens the game scene and enters play mode, so the game can be started from a single command
    /// instead of three clicks:
    ///
    ///   Unity -projectPath . -executeMethod Unseen.EditorTools.UnseenPlay.OpenAndPlay
    ///
    /// Deliberately not batch-mode only: play mode needs a real editor session, and play mode is the
    /// one thing the headless smoke test cannot cover - the animator, the client rig, the camera and
    /// the HUD only exist while the game is actually running.
    /// </summary>
    public static class UnseenPlay
    {
        private const string ScenePath = "Assets/Unseen/Scenes/Unseen_Game.unity";

        [MenuItem("Unseen/Open Game Scene And Play", priority = 31)]
        public static void OpenAndPlay()
        {
            if (Application.isBatchMode)
            {
                Debug.LogError("[Unseen] play mode needs a real editor session; drop -batchmode.");
                return;
            }

            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"[Unseen] {ScenePath} is missing - run Unseen/Setup first.");
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.Log("[Unseen] already entering play mode.");
                return;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[Unseen] opened {ScenePath}; entering play mode.");

            // Deferred by one editor tick: entering play mode in the same call as the scene load
            // races the asset database and can come up with a half-loaded scene.
            EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
        }
    }
}
