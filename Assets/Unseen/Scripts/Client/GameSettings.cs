using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unseen.Client
{
    /// <summary>
    /// Player settings, saved to a file beside the game rather than to PlayerPrefs.
    ///
    /// PlayerPrefs would be less code, but it hides the settings in the Windows registry where a
    /// player cannot find them, copy them to another machine, or fix a binding that has locked
    /// them out. A plain text file next to the save data is inspectable and repairable.
    ///
    /// Written through a temporary file and moved into place, so a crash mid-write leaves the
    /// previous settings intact instead of a truncated file that fails to parse on next launch.
    /// </summary>
    [Serializable]
    public sealed class GameSettings
    {
        public const string FileName = "settings.cfg";

        [Header("Look")]
        public float MouseSensitivity = 2.2f;
        public bool InvertY;

        [Header("Bindings")]
        public string Sprint = KeyCode.LeftShift.ToString();
        public string Crouch = KeyCode.LeftControl.ToString();
        public string Prone = KeyCode.C.ToString();
        public string Jump = KeyCode.Space.ToString();
        public string Grapple = KeyCode.F.ToString();
        public string Interact = KeyCode.E.ToString();
        public string Throw = KeyCode.Q.ToString();
        public string Heavy = KeyCode.LeftAlt.ToString();
        public string Utility1 = KeyCode.Alpha1.ToString();
        public string Utility2 = KeyCode.Alpha2.ToString();
        public string Utility3 = KeyCode.Alpha3.ToString();

        [Header("Display")]
        public float Brightness = 1f;
        public bool ShowHud = true;

        private static GameSettings _current;

        /// <summary>Raised after a load or a change, so live systems can re-read the values.</summary>
        public static event Action<GameSettings> Changed;

        public static GameSettings Current => _current ?? (_current = Load());

        public static string Path =>
            System.IO.Path.Combine(Application.persistentDataPath, FileName);

        public static GameSettings Load()
        {
            var settings = new GameSettings();

            try
            {
                if (File.Exists(Path))
                {
                    string json = File.ReadAllText(Path);
                    JsonUtility.FromJsonOverwrite(json, settings);
                    Debug.Log($"[Unseen] settings loaded from {Path}");
                }
                else
                {
                    Debug.Log($"[Unseen] no settings file; defaults will be written to {Path}");
                }
            }
            catch (Exception e)
            {
                // A corrupt settings file must never stop the game booting.
                Debug.LogWarning($"[Unseen] settings at {Path} could not be read ({e.Message}); using defaults");
                settings = new GameSettings();
            }

            settings.Sanitise();
            _current = settings;
            return settings;
        }

        public void Save()
        {
            Sanitise();

            try
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string temporary = Path + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(this, true));

                if (File.Exists(Path)) File.Delete(Path);
                File.Move(temporary, Path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Unseen] settings could not be saved to {Path}: {e.Message}");
            }

            Changed?.Invoke(this);
        }

        /// <summary>Announces the current values without writing, e.g. for a live preview.</summary>
        public void Apply() => Changed?.Invoke(this);

        public void ResetToDefaults()
        {
            var defaults = new GameSettings();
            MouseSensitivity = defaults.MouseSensitivity;
            InvertY = defaults.InvertY;
            Sprint = defaults.Sprint;
            Crouch = defaults.Crouch;
            Prone = defaults.Prone;
            Jump = defaults.Jump;
            Grapple = defaults.Grapple;
            Throw = defaults.Throw;
            Interact = defaults.Interact;
            Heavy = defaults.Heavy;
            Utility1 = defaults.Utility1;
            Utility2 = defaults.Utility2;
            Utility3 = defaults.Utility3;
            Brightness = defaults.Brightness;
            ShowHud = defaults.ShowHud;
        }

        public KeyCode Key(string name, KeyCode fallback)
        {
            return Enum.TryParse(name, out KeyCode code) ? code : fallback;
        }

        /// <summary>
        /// Clamps values and repairs unparseable bindings. A settings file is user-editable by
        /// design, so it has to survive being edited badly.
        /// </summary>
        private void Sanitise()
        {
            MouseSensitivity = Mathf.Clamp(MouseSensitivity, 0.2f, 10f);
            Brightness = Mathf.Clamp(Brightness, 0.4f, 2.5f);

            var defaults = new GameSettings();
            Sprint = Repair(Sprint, defaults.Sprint);
            Crouch = Repair(Crouch, defaults.Crouch);
            Prone = Repair(Prone, defaults.Prone);
            Jump = Repair(Jump, defaults.Jump);
            Grapple = Repair(Grapple, defaults.Grapple);
            Interact = Repair(Interact, defaults.Interact);
            Heavy = Repair(Heavy, defaults.Heavy);
            Utility1 = Repair(Utility1, defaults.Utility1);
            Utility2 = Repair(Utility2, defaults.Utility2);
            Utility3 = Repair(Utility3, defaults.Utility3);
        }

        private static string Repair(string value, string fallback)
        {
            return Enum.TryParse(value, out KeyCode _) ? value : fallback;
        }

        /// <summary>Every rebindable action, for the menu to enumerate without hard-coding a list.</summary>
        public IEnumerable<Binding> Bindings()
        {
            yield return new Binding("Sprint", () => Sprint, v => Sprint = v);
            yield return new Binding("Crouch", () => Crouch, v => Crouch = v);
            yield return new Binding("Prone", () => Prone, v => Prone = v);
            yield return new Binding("Jump", () => Jump, v => Jump = v);
            yield return new Binding("Grapple", () => Grapple, v => Grapple = v);
            yield return new Binding("Interact", () => Interact, v => Interact = v);
            yield return new Binding("Heavy attack", () => Heavy, v => Heavy = v);
            yield return new Binding("Utility 1", () => Utility1, v => Utility1 = v);
            yield return new Binding("Utility 2", () => Utility2, v => Utility2 = v);
            yield return new Binding("Utility 3", () => Utility3, v => Utility3 = v);
        }

        public readonly struct Binding
        {
            public readonly string Label;
            public readonly Func<string> Get;
            public readonly Action<string> Set;

            public Binding(string label, Func<string> get, Action<string> set)
            {
                Label = label;
                Get = get;
                Set = set;
            }
        }
    }
}
