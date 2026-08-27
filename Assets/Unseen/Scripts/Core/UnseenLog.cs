using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// Where the project's own logging goes.
    ///
    /// There were about thirty-five unconditional Debug.Log calls in the runtime assembly, and
    /// nearly all of them were generation statistics and startup notes: how many shoji were built,
    /// how many hedges, how many panels of mist, what the riverbed depths came out at. Every one of
    /// them is genuinely useful while working on the thing that prints it, and every one of them is
    /// noise in a player's log file - a build that greets its user with thirty lines of internal
    /// counts reads as unfinished.
    ///
    /// So they are gated rather than deleted. Deleting them would throw away the diagnostics that
    /// have found most of the real bugs in this project, and several of the editor probes read these
    /// exact lines out of the log to decide whether they passed. The gate defaults to ON in the
    /// editor and OFF in a player, which keeps both of those working and shuts the shipped build up.
    ///
    /// Warnings and errors are never gated. A missing audio bank or a system throwing on tick is
    /// not chatter.
    /// </summary>
    public static class UnseenLog
    {
        private static bool _resolved;
        private static bool _verbose;

        /// <summary>
        /// Whether informational logging is printed.
        ///
        /// Defaults to true in the editor and false in a player build. A dedicated server can turn
        /// it back on with <c>-unseen-verbose</c> on the command line, which is the one case where a
        /// shipped binary genuinely wants the running commentary.
        /// </summary>
        public static bool Verbose
        {
            get
            {
                if (_resolved) return _verbose;

                _verbose = Application.isEditor;

                // Checked once and cached: reading the command line is not free and this is
                // consulted from inside generation loops.
                string[] args = System.Environment.GetCommandLineArgs();

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-unseen-verbose") _verbose = true;
                    else if (args[i] == "-unseen-quiet") _verbose = false;
                }

                _resolved = true;
                return _verbose;
            }

            set
            {
                _verbose = value;
                _resolved = true;
            }
        }

        /// <summary>A note about what was built or what state was entered. Gated.</summary>
        public static void Info(string message)
        {
            if (!Verbose) return;
            Debug.Log(message);
        }

        /// <summary>Something is missing or misconfigured but the game can carry on. Never gated.</summary>
        public static void Warn(string message) => Debug.LogWarning(message);

        /// <summary>Something is broken. Never gated.</summary>
        public static void Error(string message) => Debug.LogError(message);
    }
}
