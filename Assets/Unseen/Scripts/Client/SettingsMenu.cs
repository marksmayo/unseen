using UnityEngine;

namespace Unseen.Client
{
    /// <summary>
    /// The Escape menu: look settings, rebindable keys, and a controls reference.
    ///
    /// IMGUI for the same reason the HUD is: it needs no prefabs, no canvas and no art, and it
    /// cannot get out of sync with the settings behind it. It also takes over Escape from
    /// <see cref="PlayerInputSource"/>, which previously used the key to toggle the cursor - the
    /// menu now owns both the cursor and the input gate while it is open.
    ///
    /// Drawn entirely through <see cref="UnseenUi"/>. It used to be stock IMGUI - grey gradient
    /// buttons, the built-in slider, a flat black box - which is fine for a tool and reads as a
    /// debug overlay in a game. Every control here is hand-drawn now, and the stock widgets survive
    /// only underneath as invisible hit targets, because their behaviour was never the problem.
    /// </summary>
    public sealed class SettingsMenu : MonoBehaviour
    {
        public PlayerInputSource Input;

        [Tooltip("Seconds the panel takes to fade in. Nothing should appear instantly.")]
        public float FadeSeconds = 0.12f;

        public bool IsOpen { get; private set; }

        private GameSettings _settings;
        private GameSettings.Binding _rebinding;
        private bool _awaitingKey;
        private Vector2 _scroll;
        private float _fade;
        private Tab _tab = Tab.Controls;

        private enum Tab
        {
            Controls,
            Display,
            Reference
        }

        private void Awake()
        {
            _settings = GameSettings.Current;
            _settings.Apply();
        }

        private void Update()
        {
            _fade = Mathf.MoveTowards(_fade, IsOpen ? 1f : 0f,
                Time.unscaledDeltaTime / Mathf.Max(0.01f, FadeSeconds));

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
            if (_fade <= 0.001f) return;

            float a = UnseenUi.Ease(_fade);

            UnseenUi.Fill(new Rect(0f, 0f, Screen.width, Screen.height),
                UnseenUi.Scrim * new Color(1f, 1f, 1f, a));

            const float width = 620f;
            const float height = 560f;

            // Rising slightly as it fades in. Eight pixels of travel, which is not enough to
            // notice and is exactly enough to feel.
            var panel = new Rect(
                Mathf.Round(Screen.width * 0.5f - width * 0.5f),
                Mathf.Round(Screen.height * 0.5f - height * 0.5f + (1f - a) * 8f),
                width, height);

            UnseenUi.Panel(panel, a);
            UnseenUi.Accented(panel, UnseenUi.Accent, a);

            float x = panel.x + UnseenUi.Pad;
            float inner = panel.width - UnseenUi.Pad * 2f;

            UnseenUi.Say(new Rect(x, panel.y + 22f, inner, 30f), "UNSEEN", UnseenUi.Title,
                UnseenUi.Text, a);
            UnseenUi.Say(new Rect(x, panel.y + 48f, inner, 18f), "settings", UnseenUi.Caption,
                UnseenUi.Faint, a);

            DrawTabs(new Rect(x, panel.y + 78f, inner, 30f));

            var body = new Rect(x, panel.y + 122f, inner, panel.height - 122f - 66f);

            GUILayout.BeginArea(body);
            _scroll = GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none);

            switch (_tab)
            {
                case Tab.Controls: DrawBindings(inner); break;
                case Tab.Display: DrawLook(inner); break;
                default: DrawReference(inner); break;
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            DrawFooter(new Rect(x, panel.yMax - 52f, inner, 32f));
        }

        private void DrawTabs(Rect rect)
        {
            string[] names = { "Controls", "Display", "Reference" };
            float w = rect.width / names.Length;

            for (int i = 0; i < names.Length; i++)
            {
                var slot = new Rect(rect.x + w * i, rect.y, w - 6f, rect.height);
                bool active = (int)_tab == i;

                if (active)
                {
                    UnseenUi.Rounded(slot, new Color(1f, 1f, 1f, 0.09f), UnseenUi.SmallRadius);
                    UnseenUi.Rounded(new Rect(slot.x + 6f, slot.yMax - 2f, slot.width - 12f, 2f),
                        UnseenUi.Accent, 1f);
                }

                var centred = new GUIStyle(UnseenUi.Body)
                {
                    alignment = TextAnchor.MiddleCenter
                };

                bool over = slot.Contains(Event.current.mousePosition);
                UnseenUi.Say(slot, names[i], centred,
                    active ? UnseenUi.Text : over ? UnseenUi.Muted : UnseenUi.Faint);

                if (GUI.Button(slot, GUIContent.none, GUIStyle.none)) _tab = (Tab)i;
            }
        }

        private void DrawLook(float width)
        {
            Group("Look");

            _settings.MouseSensitivity = Row(width, "Mouse sensitivity",
                $"{_settings.MouseSensitivity:0.0}", _settings.MouseSensitivity, 0.2f, 10f);

            _settings.Brightness = Row(width, "Brightness",
                $"{_settings.Brightness:0.00}", _settings.Brightness, 0.4f, 2.5f);

            GUILayout.Space(6f);

            _settings.InvertY = Check(width, _settings.InvertY, "Invert vertical look");
            _settings.ShowHud = Check(width, _settings.ShowHud, "Show the HUD");

            // Live preview: applied as the slider moves, and only written to disk on close.
            _settings.Apply();
        }

        private void DrawBindings(float width)
        {
            Group("Movement and actions");

            foreach (GameSettings.Binding binding in _settings.Bindings())
            {
                Rect line = GUILayoutUtility.GetRect(width, UnseenUi.Row);

                bool over = line.Contains(Event.current.mousePosition);
                if (over) UnseenUi.Rounded(line, UnseenUi.Raised, UnseenUi.SmallRadius);

                UnseenUi.Say(new Rect(line.x + 8f, line.y, width * 0.55f, line.height),
                    binding.Label, UnseenUi.Body, UnseenUi.Muted);

                bool waiting = _awaitingKey && _rebinding.Label == binding.Label;
                string caption = waiting ? "press a key" : Pretty(binding.Get());

                var key = new Rect(line.xMax - 150f, line.y + 3f, 142f, line.height - 6f);

                if (waiting)
                {
                    UnseenUi.Rounded(key, UnseenUi.Accent * new Color(1f, 1f, 1f, 0.35f),
                        UnseenUi.SmallRadius, UnseenUi.Accent, 1f);

                    var mid = new GUIStyle(UnseenUi.Body)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontStyle = FontStyle.Italic
                    };

                    UnseenUi.Say(key, caption, mid, UnseenUi.Text);
                }
                else if (UnseenUi.Button(key, caption) && !_awaitingKey)
                {
                    _rebinding = binding;
                    _awaitingKey = true;
                }
            }

            GUILayout.Space(8f);

            UnseenUi.Say(GUILayoutUtility.GetRect(width, 20f),
                _awaitingKey
                    ? "Escape cancels the rebind."
                    : "Attack and guard stay on the mouse and cannot be rebound.",
                UnseenUi.Caption, UnseenUi.Faint);
        }

        private void DrawReference(float width)
        {
            Group("Fixed controls");

            Reference(width, "Move", "W A S D");
            Reference(width, "Light attack", "Left mouse");
            Reference(width, "Heavy attack", "Heavy key and left mouse");
            Reference(width, "Guard and parry", "Right mouse, held");
            Reference(width, "Guard zone", "Where you look: high, level, low");
            Reference(width, "Takedown", "Attack from behind someone unaware");
            Reference(width, "Map", "M");
            Reference(width, "Spectate next", "Jump, once eliminated");

            GUILayout.Space(10f);
            Group("How it works");

            Note(width, "Sound is the game. Sprinting carries a long way, crouching barely " +
                        "carries at all, and startling a bird announces you from further than " +
                        "either.");
            Note(width, "Douse a lantern to make a route dark, and light it again to take that " +
                        "route away from somebody else.");
            Note(width, "Shuriken never run out, but there are five seconds between throws and " +
                        "every one of them whistles the whole way.");
            Note(width, "Deep water hides you completely if you go prone in it. You have about " +
                        "thirty seconds of air before you start giving yourself away.");
        }

        private void DrawFooter(Rect rect)
        {
            UnseenUi.Fill(new Rect(rect.x, rect.y - 14f, rect.width, 1f),
                new Color(1f, 1f, 1f, 0.07f));

            if (UnseenUi.Button(new Rect(rect.x, rect.y, 150f, rect.height), "Reset to defaults"))
            {
                _settings.ResetToDefaults();
                _settings.Save();
            }

            if (UnseenUi.Button(new Rect(rect.xMax - 110f, rect.y, 110f, rect.height), "Close",
                    primary: true))
                Toggle();

            UnseenUi.Say(new Rect(rect.x + 162f, rect.y, rect.width - 284f, rect.height),
                "Escape closes", UnseenUi.Caption, UnseenUi.Faint);
        }

        // ------------------------------------------------------------------ row helpers

        private static void Group(string name)
        {
            GUILayout.Space(4f);
            UnseenUi.Heading(GUILayoutUtility.GetRect(10f, 22f), name);
            GUILayout.Space(4f);
        }

        private static float Row(float width, string label, string readout, float value,
            float min, float max)
        {
            Rect line = GUILayoutUtility.GetRect(width, UnseenUi.Row);

            UnseenUi.Say(new Rect(line.x + 8f, line.y, width * 0.42f, line.height), label,
                UnseenUi.Body, UnseenUi.Muted);

            UnseenUi.Say(new Rect(line.x + width * 0.42f, line.y, 54f, line.height), readout,
                UnseenUi.Number, UnseenUi.Text);

            var track = new Rect(line.x + width * 0.42f + 66f, line.y,
                width - width * 0.42f - 74f, line.height);

            return UnseenUi.Slider(track, value, min, max);
        }

        private static bool Check(float width, bool value, string label)
        {
            Rect line = GUILayoutUtility.GetRect(width, UnseenUi.Row);
            return UnseenUi.Toggle(new Rect(line.x + 8f, line.y, width - 16f, line.height),
                value, label);
        }

        private static void Reference(float width, string what, string how)
        {
            Rect line = GUILayoutUtility.GetRect(width, 24f);

            UnseenUi.Say(new Rect(line.x + 8f, line.y, width * 0.45f, line.height), what,
                UnseenUi.Label, UnseenUi.Muted);

            UnseenUi.Say(new Rect(line.x + width * 0.45f, line.y, width * 0.55f - 8f, line.height),
                how, UnseenUi.Label, UnseenUi.Text);
        }

        private static void Note(float width, string text)
        {
            var style = new GUIStyle(UnseenUi.Caption) { wordWrap = true };
            float height = style.CalcHeight(new GUIContent(text), width - 24f);

            Rect line = GUILayoutUtility.GetRect(width, height + 10f);

            UnseenUi.Fill(new Rect(line.x + 8f, line.y + 3f, 2f, height),
                UnseenUi.Accent * new Color(1f, 1f, 1f, 0.55f));

            UnseenUi.Say(new Rect(line.x + 20f, line.y, width - 28f, height), text, style,
                UnseenUi.Muted);
        }

        private static string Pretty(string key)
        {
            if (string.IsNullOrEmpty(key)) return "unbound";
            if (key.StartsWith("Alpha")) return key.Substring(5);
            if (key.StartsWith("Left")) return "Left " + key.Substring(4);
            if (key.StartsWith("Right")) return "Right " + key.Substring(5);
            return key;
        }
    }
}
