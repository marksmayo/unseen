using UnityEngine;

namespace Unseen.Client
{
    /// <summary>
    /// The Escape menu: look settings, rebindable keys, brightness, and a controls reference.
    ///
    /// IMGUI for the same reason the HUD is: it needs no prefabs, no canvas and no art, and this is
    /// a menu whose job is to be correct rather than beautiful. It also takes over Escape from
    /// <see cref="PlayerInputSource"/>, which previously used the key to toggle the cursor - the
    /// menu now owns both the cursor and the input gate while it is open.
    /// </summary>
    public sealed class SettingsMenu : MonoBehaviour
    {
        public PlayerInputSource Input;

        public bool IsOpen { get; private set; }

        private GameSettings _settings;
        private GameSettings.Binding _rebinding;
        private bool _awaitingKey;
        private Vector2 _scroll;
        private GUIStyle _title;
        private GUIStyle _row;
        private GUIStyle _hint;
        private Texture2D _white;

        private void Awake()
        {
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();

            _settings = GameSettings.Current;
            _settings.Apply();
        }

        private void Update()
        {
            // While waiting for a key, Escape cancels the rebind rather than closing the menu -
            // otherwise there is no way to back out of a rebind you did not mean to start.
            if (_awaitingKey)
            {
                CaptureBinding();
                return;
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) Toggle();
        }

        public void Toggle()
        {
            IsOpen = !IsOpen;

            if (Input != null) Input.AcceptInput = !IsOpen;

            Cursor.lockState = IsOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = IsOpen;

            if (Input != null) Input.LockCursor = !IsOpen;
            if (!IsOpen) _settings.Save();
        }

        private void CaptureBinding()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                _awaitingKey = false;
                return;
            }

            if (!UnityEngine.Input.anyKeyDown) return;

            foreach (KeyCode code in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (!UnityEngine.Input.GetKeyDown(code)) continue;

                // Mouse buttons are fixed: attack and guard are on them by design, and letting
                // someone bind sprint to the fire button would break more than it fixed.
                if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6) continue;

                _rebinding.Set?.Invoke(code.ToString());
                _awaitingKey = false;
                _settings.Save();
                return;
            }
        }

        private void OnGUI()
        {
            if (!IsOpen) return;

            EnsureStyles();

            var panel = new Rect(Screen.width * 0.5f - 260f, Screen.height * 0.5f - 250f, 520f, 500f);
            Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.55f));
            Fill(panel, new Color(0.06f, 0.07f, 0.10f, 0.96f));

            GUILayout.BeginArea(new Rect(panel.x + 22f, panel.y + 18f, panel.width - 44f, panel.height - 36f));

            GUILayout.Label("Settings", _title);
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll);

            DrawLook();
            GUILayout.Space(10f);
            DrawBindings();
            GUILayout.Space(10f);
            DrawFixedControls();

            GUILayout.EndScrollView();

            GUILayout.Space(8f);
            DrawFooter();

            GUILayout.EndArea();
        }

        private void DrawLook()
        {
            GUILayout.Label("LOOK", _row);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Mouse sensitivity  {_settings.MouseSensitivity:0.0}", GUILayout.Width(200f));
            float sensitivity = GUILayout.HorizontalSlider(_settings.MouseSensitivity, 0.2f, 10f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Brightness  {_settings.Brightness:0.00}", GUILayout.Width(200f));
            float brightness = GUILayout.HorizontalSlider(_settings.Brightness, 0.4f, 2.5f);
            GUILayout.EndHorizontal();

            bool invert = GUILayout.Toggle(_settings.InvertY, "  Invert vertical look");
            bool hud = GUILayout.Toggle(_settings.ShowHud, "  Show HUD");

            // Live preview: apply as the slider moves, and only write the file on close.
            if (!Mathf.Approximately(sensitivity, _settings.MouseSensitivity) ||
                !Mathf.Approximately(brightness, _settings.Brightness) ||
                invert != _settings.InvertY || hud != _settings.ShowHud)
            {
                _settings.MouseSensitivity = sensitivity;
                _settings.Brightness = brightness;
                _settings.InvertY = invert;
                _settings.ShowHud = hud;
                _settings.Apply();
            }
        }

        private void DrawBindings()
        {
            GUILayout.Label("KEYS", _row);

            foreach (GameSettings.Binding binding in _settings.Bindings())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(binding.Label, GUILayout.Width(200f));

                bool waiting = _awaitingKey && _rebinding.Label == binding.Label;
                string caption = waiting ? "press a key..." : Pretty(binding.Get());

                if (GUILayout.Button(caption, GUILayout.Width(160f)) && !_awaitingKey)
                {
                    _rebinding = binding;
                    _awaitingKey = true;
                }

                GUILayout.EndHorizontal();
            }

            if (_awaitingKey) GUILayout.Label("Escape cancels the rebind.", _hint);
        }

        private void DrawFixedControls()
        {
            GUILayout.Label("FIXED", _row);
            GUILayout.Label("Move                W A S D", _hint);
            GUILayout.Label("Light attack        Left mouse", _hint);
            GUILayout.Label("Heavy attack        Heavy key + left mouse", _hint);
            GUILayout.Label("Guard / parry       Right mouse (held)", _hint);
            GUILayout.Label("Guard zone          Where you look: up high, level mid, down low", _hint);
            GUILayout.Label("Takedown            Attack from behind an unaware enemy", _hint);
            GUILayout.Label("Prone               C (rebindable above)", _hint);
            GUILayout.Label("Throw shuriken      Q (rebindable above) - one every 2 s", _hint);
            GUILayout.Label("Map zoom            M", _hint);
            GUILayout.Label("Spectate next       Jump key, once eliminated", _hint);
            GUILayout.Label("Debug overlay       F3", _hint);
        }

        private void DrawFooter()
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Reset to defaults", GUILayout.Height(28f)))
            {
                _settings.ResetToDefaults();
                _settings.Save();
            }

            if (GUILayout.Button("Close", GUILayout.Height(28f))) Toggle();

            GUILayout.EndHorizontal();
            GUILayout.Label($"Saved to {GameSettings.Path}", _hint);
        }

        private static string Pretty(string key)
        {
            if (key.StartsWith("Alpha")) return key.Substring(5);
            if (key.StartsWith("Left")) return "L " + key.Substring(4);
            if (key.StartsWith("Right")) return "R " + key.Substring(5);
            return key;
        }

        private void EnsureStyles()
        {
            if (_title != null) return;

            _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            _row = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _hint = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        }

        private void Fill(Rect rect, Color colour)
        {
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _white);
            GUI.color = previous;
        }
    }
}
