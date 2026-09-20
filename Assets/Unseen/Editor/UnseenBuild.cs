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

            // Stamped before the player is built, so the binary and the stamp cannot disagree.
            StampThisBuild();

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

        /// <summary>
        /// Writes which build this is, for the binary to read back at startup.
        ///
        /// A resource rather than generated code. Generated source has to exist before compilation,
        /// which is before the build that is meant to produce it - a loop with a sharp edge, where
        /// forgetting once leaves the binary confidently reporting the commit before the one it
        /// actually contains. A file written here is written and read in the order it is built.
        ///
        /// Never fails the build. A machine without git on its path, or a source tree unpacked from
        /// an archive, should produce an unstamped build rather than no build - the stamp is a
        /// convenience for diagnosis and not a thing worth refusing to ship over.
        /// </summary>
        public static void StampThisBuild()
        {
            string commit = Git("rev-parse --short HEAD");
            bool dirty = !string.IsNullOrEmpty(Git("status --porcelain"));

            if (string.IsNullOrEmpty(commit))
            {
                Debug.LogWarning("[Unseen] no git commit available; this build will not identify itself");
                commit = string.Empty;
            }

            string version = PlayerSettings.bundleVersion;
            if (string.IsNullOrEmpty(version)) version = "0.0.0";

            string text = Unseen.Core.BuildStamp.Write(
                version, commit, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), dirty);

            const string directory = "Assets/Unseen/Resources";
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Unseen.Core.BuildStamp.ResourcePath + ".txt"), text);

            AssetDatabase.Refresh();

            Debug.Log($"[Unseen] stamped {version} {commit}{(dirty ? "+" : string.Empty)}");
        }

        /// <summary>Runs a git command and returns its output, or empty if git is not available.</summary>
        private static string Git(string arguments)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
                    {
                        WorkingDirectory = Path.GetDirectoryName(Application.dataPath),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();

                // Bounded, because a build must not hang on a git command that is waiting for
                // something - credentials, a lock, a prompt nobody will ever answer.
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    return string.Empty;
                }

                return process.ExitCode == 0 ? output.Trim() : string.Empty;
            }
            catch
            {
                // No git on the path, or a sandbox that will not spawn processes.
                return string.Empty;
            }
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
