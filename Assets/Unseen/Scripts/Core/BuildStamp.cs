using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// Which build this is: version, commit, when, and whether the tree was clean.
    ///
    /// Three bugs in one day were "works in the editor, broken in the build" - a mesh imported
    /// without Read/Write, two shaders stripped for being reached only by name. Each was found by
    /// running a binary and reading its log, and not one of those logs said which binary it was.
    ///
    /// That is survivable while the only person running builds made them ten minutes ago. It stops
    /// being survivable the first time somebody says "it did it again on the build from Tuesday",
    /// and there were four builds on Tuesday.
    ///
    /// Read from a text file in Resources rather than generated into code. A generated source file
    /// has to be written before compilation and therefore before the build that is meant to produce
    /// it, which is a loop with a sharp edge: forget once and the binary confidently reports the
    /// commit before the one it contains. A resource is written and read in the same order it is
    /// built, and a missing one is obviously missing rather than quietly stale.
    /// </summary>
    public struct BuildStamp
    {
        /// <summary>Where the stamp lives. Written by the build, read at startup.</summary>
        public const string ResourcePath = "BuildStamp";

        public string Version;
        public string Commit;
        public string BuiltAt;

        /// <summary>
        /// Whether the working tree had uncommitted changes.
        ///
        /// The commit alone is a lie for a build made from a dirty tree, and that is how most
        /// builds are made. Somebody chasing a bug back to a1b2c3d would be reading code that was
        /// never in the binary they were running.
        /// </summary>
        public bool Dirty;

        /// <summary>True when this identifies a build at all.</summary>
        public bool IsKnown => !string.IsNullOrEmpty(Version) && !string.IsNullOrEmpty(Commit);

        /// <summary>Short enough for a corner of the menu and one line of a log.</summary>
        public string Describe()
        {
            if (!IsKnown) return "unknown build";

            return Dirty ? $"{Version} {Commit}+" : $"{Version} {Commit}";
        }

        public static string Write(string version, string commit, string builtAt, bool dirty)
        {
            return $"version={version}\ncommit={commit}\nbuilt={builtAt}\ndirty={(dirty ? "1" : "0")}\n";
        }

        /// <summary>
        /// Reads a stamp, or returns one that admits it knows nothing.
        ///
        /// Never throws and never salvages. A half-written file or a resource that is not this one
        /// should produce "unknown build" rather than a version with no commit behind it - a
        /// confident wrong answer is worse here than no answer, because the whole purpose is to be
        /// the thing somebody trusts when they are trying to work out what they ran.
        /// </summary>
        public static BuildStamp Read(string text)
        {
            var stamp = new BuildStamp();
            if (string.IsNullOrEmpty(text)) return stamp;

            string[] lines = text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int split = line.IndexOf('=');
                if (split <= 0) continue;

                string key = line.Substring(0, split);
                string value = line.Substring(split + 1);

                switch (key)
                {
                    case "version": stamp.Version = value; break;
                    case "commit": stamp.Commit = value; break;
                    case "built": stamp.BuiltAt = value; break;
                    case "dirty": stamp.Dirty = value == "1"; break;
                }
            }

            // All or nothing. A version without a commit names a release rather than a build, and
            // it is a build somebody is trying to identify.
            if (!stamp.IsKnown) return default;

            return stamp;
        }

        private static bool _loaded;
        private static BuildStamp _current;

        /// <summary>This build's stamp, loaded once.</summary>
        public static BuildStamp Current
        {
            get
            {
                if (_loaded) return _current;
                _loaded = true;

                var asset = Resources.Load<TextAsset>(ResourcePath);
                _current = Read(asset != null ? asset.text : null);

                if (asset != null) Resources.UnloadAsset(asset);
                return _current;
            }
        }
    }
}
