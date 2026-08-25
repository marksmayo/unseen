using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Batch-mode build entry points for CI and for producing the headless Linux server image.
    ///
    /// Example:
    ///   Unity -quit -batchmode -nographics -projectPath . \
    ///         -executeMethod Unseen.EditorTools.UnseenBuild.BuildLinuxServer \
    ///         -buildOutput Server/out/linux
    /// </summary>
    public static class UnseenBuild
    {
        private const string DefaultServerOutput = "Server/out/linux";
        private const string DefaultClientOutput = "Server/out/client";

        [MenuItem("Unseen/Build/Linux Headless Server", priority = 60)]
        public static void BuildLinuxServer()
        {
            string output = Path.Combine(ArgumentOr("-buildOutput", DefaultServerOutput), "unseen-server");
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;

            var options = new BuildPlayerOptions
            {
                scenes = EnabledScenes(),
                locationPathName = output,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None
            };

            Run(options, "linux headless server");
        }

        [MenuItem("Unseen/Build/Windows Client", priority = 61)]
        public static void BuildWindowsClient()
        {
            string output = Path.Combine(ArgumentOr("-buildOutput", DefaultClientOutput), "unseen.exe");
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;

            var options = new BuildPlayerOptions
            {
                scenes = EnabledScenes(),
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None
            };

            Run(options, "windows client");
        }

        private static void Run(BuildPlayerOptions options, string label)
        {
            if (options.scenes == null || options.scenes.Length == 0)
            {
                Fail($"no enabled scenes in build settings - run Unseen/Setup/Run All Setup Steps first");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(options.locationPathName) ?? ".");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Unseen] {label} build succeeded: {options.locationPathName} " +
                          $"({summary.totalSize / (1024 * 1024)} MiB, {summary.totalTime.TotalSeconds:0} s)");
                return;
            }

            Fail($"{label} build {summary.result} with {summary.totalErrors} errors");
        }

        private static void Fail(string message)
        {
            Debug.LogError($"[Unseen] {message}");
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }

        private static string[] EnabledScenes()
        {
            var scenes = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                if (scene.enabled)
                    scenes.Add(scene.path);
            return scenes.ToArray();
        }

        private static string ArgumentOr(string flag, string fallback)
        {
            // Fully qualified: the project has an Unseen.Environment namespace, which the compiler
            // finds before System when this file sits inside the Unseen.* namespace tree.
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }
    }
}
