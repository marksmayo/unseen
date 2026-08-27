using UnityEngine;

namespace Unseen.Client
{
    /// <summary>
    /// The look of the interface, in one place.
    ///
    /// Everything on screen was default IMGUI: flat black rectangles, stock grey buttons, and the
    /// built-in skin's font at whatever size each call site happened to pick. It read as a debug
    /// overlay, which is what it was.
    ///
    /// This is not a UI framework and does not want to be one. IMGUI is the right choice here for
    /// the same reasons it always was - no prefabs, no canvas, no art dependencies, and a HUD that
    /// cannot get out of sync with the snapshot driving it. What was missing was a shared visual
    /// language, so that is what this is: one palette, one type scale, one set of spacings, and a
    /// handful of drawing primitives that every surface goes through.
    ///
    /// The rounded corners and hairline borders are real, not faked with sliced textures.
    /// GUI.DrawTexture has an overload taking border widths and corner radii, which is doing all
    /// the work here and is the single thing that lifts this out of looking like a debug menu.
    /// </summary>
    public static class UnseenUi
    {
        // ------------------------------------------------------------------ palette
        //
        // Taken from the game rather than invented: the indigo and charcoal of the ninja cloth, the
        // bone of shoji paper, and vermilion off the bridge rails. Nothing here is pure black or
        // pure white - a night scene has neither, and an interface that does looks pasted on.

        /// <summary>Panel ground. Deep indigo, nearly opaque.</summary>
        public static readonly Color Ink = new Color(0.055f, 0.065f, 0.095f, 0.94f);

        /// <summary>A raised surface inside a panel: a row, a field, a table stripe.</summary>
        public static readonly Color Raised = new Color(1f, 1f, 1f, 0.045f);

        /// <summary>Hairline border. Light, and barely there.</summary>
        public static readonly Color Edge = new Color(1f, 1f, 1f, 0.13f);

        /// <summary>The screen behind an open menu.</summary>
        public static readonly Color Scrim = new Color(0.02f, 0.025f, 0.04f, 0.72f);

        /// <summary>Primary text. Bone, not white.</summary>
        public static readonly Color Text = new Color(0.93f, 0.94f, 0.96f);

        /// <summary>Secondary text: labels, units, hints.</summary>
        public static readonly Color Muted = new Color(0.62f, 0.65f, 0.72f);

        /// <summary>Tertiary text: file paths, footnotes.</summary>
        public static readonly Color Faint = new Color(0.42f, 0.45f, 0.52f);

        /// <summary>The accent. Vermilion, off the bridge rails.</summary>
        public static readonly Color Accent = new Color(0.83f, 0.31f, 0.24f);

        /// <summary>Won, survived, safe.</summary>
        public static readonly Color Gold = new Color(0.94f, 0.78f, 0.42f);

        /// <summary>Hurt.</summary>
        public static readonly Color Blood = new Color(0.74f, 0.22f, 0.20f);

        /// <summary>Hidden, quiet, cool.</summary>
        public static readonly Color Cool = new Color(0.44f, 0.68f, 0.86f);

        // ------------------------------------------------------------------ metrics

        /// <summary>Corner radius for a panel, in points.</summary>
        public const float Radius = 7f;

        /// <summary>Corner radius for a control inside a panel.</summary>
        public const float SmallRadius = 4f;

        /// <summary>Standard inner margin.</summary>
        public const float Pad = 20f;

        /// <summary>Height of a settings row or a table line.</summary>
        public const float Row = 30f;

        // ------------------------------------------------------------------ type
        //
        // A scale rather than a size per call site. Built once, lazily, because GUI.skin is not
        // available until the first OnGUI.

        private static GUIStyle _display, _title, _section, _body, _label, _caption, _number;
        private static Texture2D _white;
        private static bool _built;

        /// <summary>Match results, the eliminated banner: the largest thing on screen.</summary>
        public static GUIStyle Display => Built()._display;

        /// <summary>Panel titles.</summary>
        public static GUIStyle Title => Built()._title;

        /// <summary>A group heading inside a panel. Small, bold, letter-spaced by hand.</summary>
        public static GUIStyle Section => Built()._section;

        /// <summary>Ordinary interface text.</summary>
        public static GUIStyle Body => Built()._body;

        /// <summary>A field label, muted.</summary>
        public static GUIStyle Label => Built()._label;

        /// <summary>Hints and footnotes.</summary>
        public static GUIStyle Caption => Built()._caption;

        /// <summary>Right-aligned figures, for tables and readouts.</summary>
        public static GUIStyle Number => Built()._number;

        private sealed class Styles
        {
            public GUIStyle _display, _title, _section, _body, _label, _caption, _number;
        }

        private static Styles _styles;

        private static Styles Built()
        {
            if (_built && _styles != null) return _styles;

            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _white.hideFlags = HideFlags.HideAndDontSave;

            GUIStyle basis = GUI.skin != null ? GUI.skin.label : new GUIStyle();

            _styles = new Styles
            {
                _display = new GUIStyle(basis)
                {
                    fontSize = 30,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft
                },
                _title = new GUIStyle(basis)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft
                },
                _section = new GUIStyle(basis)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft
                },
                _body = new GUIStyle(basis)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleLeft
                },
                _label = new GUIStyle(basis)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleLeft
                },
                _caption = new GUIStyle(basis)
                {
                    fontSize = 11,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true
                },
                _number = new GUIStyle(basis)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleRight
                }
            };

            _built = true;
            return _styles;
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>A flat rectangle. The floor everything else is built on.</summary>
        public static void Fill(Rect rect, Color colour)
        {
            Built();

            Color previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _white);
            GUI.color = previous;
        }

        /// <summary>
        /// A rounded rectangle, optionally with a hairline border.
        ///
        /// This is the whole trick. GUI.DrawTexture takes border widths and corner radii as its
        /// last two arguments, so a rounded panel with a one-pixel edge costs one draw call and no
        /// art at all.
        /// </summary>
        public static void Rounded(Rect rect, Color fill, float radius,
            Color border = default, float borderWidth = 0f)
        {
            Built();

            GUI.DrawTexture(rect, _white, ScaleMode.StretchToFill, true, 0f, fill,
                Vector4.zero, Vector4.one * radius);

            if (borderWidth <= 0f) return;

            GUI.DrawTexture(rect, _white, ScaleMode.StretchToFill, true, 0f, border,
                Vector4.one * borderWidth, Vector4.one * radius);
        }

        /// <summary>
        /// A panel: rounded, bordered, and sitting on a soft shadow.
        ///
        /// The shadow is three offset rounded rects rather than a blurred texture. Crude, cheap,
        /// and enough to lift the panel off a busy night scene, which is the only thing it is for.
        /// </summary>
        public static void Panel(Rect rect, float alpha = 1f)
        {
            Built();

            for (int i = 3; i >= 1; i--)
            {
                var shadow = new Rect(rect.x - i, rect.y - i * 0.5f + 3f,
                    rect.width + i * 2f, rect.height + i * 2f);

                Rounded(shadow, new Color(0f, 0f, 0f, 0.10f * alpha), Radius + i);
            }

            Rounded(rect, Ink * new Color(1f, 1f, 1f, alpha), Radius,
                Edge * new Color(1f, 1f, 1f, alpha), 1f);
        }

        /// <summary>The accent rule that runs along the top of a panel, under the title.</summary>
        public static void Accented(Rect panel, Color accent, float alpha = 1f)
        {
            Rounded(new Rect(panel.x + Radius, panel.y, panel.width - Radius * 2f, 2f),
                accent * new Color(1f, 1f, 1f, alpha), 1f);
        }

        /// <summary>Text, in a colour, without the caller touching GUI.color.</summary>
        public static void Say(Rect rect, string text, GUIStyle style, Color colour,
            float alpha = 1f)
        {
            Color previous = GUI.color;
            GUI.color = colour * new Color(1f, 1f, 1f, alpha);
            GUI.Label(rect, text, style);
            GUI.color = previous;
        }

        /// <summary>
        /// A meter: a rounded track with a rounded fill, and a hairline at the current value.
        ///
        /// Used for health, for how hidden you are, and for the countdown between matches. The
        /// hairline is what makes a slow-moving bar readable - without it a bar at 62% and a bar at
        /// 66% look identical.
        /// </summary>
        public static void Meter(Rect rect, float fraction, Color colour, float alpha = 1f)
        {
            fraction = Mathf.Clamp01(fraction);

            Rounded(rect, new Color(1f, 1f, 1f, 0.10f * alpha), rect.height * 0.5f);

            if (fraction <= 0.001f) return;

            float width = Mathf.Max(rect.height, rect.width * fraction);

            Rounded(new Rect(rect.x, rect.y, width, rect.height),
                colour * new Color(1f, 1f, 1f, alpha), rect.height * 0.5f);

            Fill(new Rect(rect.x + width - 1f, rect.y - 1f, 1f, rect.height + 2f),
                new Color(1f, 1f, 1f, 0.5f * alpha));
        }

        /// <summary>
        /// A button that responds to the pointer. Returns true on the frame it is clicked.
        ///
        /// Hand-drawn rather than GUI.Button, because the stock one brings the built-in skin's grey
        /// gradient with it and there is no way to talk it out of it without a full GUISkin asset.
        /// </summary>
        public static bool Button(Rect rect, string label, bool primary = false)
        {
            Built();

            bool over = rect.Contains(Event.current.mousePosition);
            bool held = over && Event.current.type == EventType.MouseDrag;

            Color fill = primary
                ? Accent * new Color(1f, 1f, 1f, over ? 1f : 0.86f)
                : new Color(1f, 1f, 1f, over ? 0.16f : 0.075f);

            if (held) fill *= new Color(0.85f, 0.85f, 0.85f, 1f);

            Rounded(rect, fill, SmallRadius, over ? Edge : Edge * 0.5f, 1f);

            var centred = new GUIStyle(Body) { alignment = TextAnchor.MiddleCenter };
            Say(rect, label, centred, primary ? Text : (over ? Text : Muted));

            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        /// <summary>A slider with a filled track and a round grip.</summary>
        public static float Slider(Rect rect, float value, float min, float max)
        {
            Built();

            var track = new Rect(rect.x, rect.y + rect.height * 0.5f - 3f, rect.width, 6f);
            float fraction = Mathf.InverseLerp(min, max, value);

            Rounded(track, new Color(1f, 1f, 1f, 0.10f), 3f);
            Rounded(new Rect(track.x, track.y, track.width * fraction, track.height), Accent, 3f);

            var grip = new Rect(track.x + track.width * fraction - 7f,
                rect.y + rect.height * 0.5f - 7f, 14f, 14f);

            bool over = rect.Contains(Event.current.mousePosition);
            Rounded(grip, over ? Text : new Color(0.82f, 0.84f, 0.88f), 7f);

            // The stock slider underneath still does the dragging; only its skin was ever the
            // problem. The thumb style has to have a SIZE though, even drawing nothing - Unity
            // hit-tests the grab against the thumb's rect, and GUIStyle.none gives it a zero-width
            // one that cannot be caught hold of.
            return GUI.HorizontalSlider(rect, value, min, max, GUIStyle.none, InvisibleThumb);
        }

        private static GUIStyle _thumb;

        /// <summary>
        /// A thumb that occupies space and draws nothing, so the stock slider has something to
        /// hit-test while this file does the drawing.
        /// </summary>
        private static GUIStyle InvisibleThumb =>
            _thumb ??= new GUIStyle { fixedWidth = 14f, fixedHeight = 14f };

        /// <summary>A checkbox and its label, as one clickable row.</summary>
        public static bool Toggle(Rect rect, bool value, string label)
        {
            Built();

            bool over = rect.Contains(Event.current.mousePosition);

            var box = new Rect(rect.x, rect.y + rect.height * 0.5f - 8f, 16f, 16f);

            Rounded(box, value ? Accent : new Color(1f, 1f, 1f, over ? 0.16f : 0.08f),
                SmallRadius, Edge, 1f);

            if (value)
            {
                // A tick, as two strokes. Drawing a glyph would mean depending on the font having
                // one, and the built-in font's check mark is a box on some platforms.
                Fill(new Rect(box.x + 4f, box.y + 8f, 4f, 2f), Text);
                Fill(new Rect(box.x + 7f, box.y + 5f, 2f, 6f), Text);
            }

            Say(new Rect(rect.x + 26f, rect.y, rect.width - 26f, rect.height), label, Body,
                over ? Text : Muted);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) return !value;
            return value;
        }

        /// <summary>
        /// A group heading: small bold text with a rule running out to the right of it.
        ///
        /// The rule is what turns a bold word into a section, and it costs one rectangle.
        /// </summary>
        public static void Heading(Rect rect, string text)
        {
            Built();

            Say(rect, text.ToUpperInvariant(), Section, Faint);

            float textWidth = Section.CalcSize(new GUIContent(text.ToUpperInvariant())).x;
            float start = rect.x + textWidth + 10f;

            if (start >= rect.xMax) return;

            Fill(new Rect(start, rect.y + rect.height * 0.5f, rect.xMax - start, 1f),
                new Color(1f, 1f, 1f, 0.09f));
        }

        /// <summary>
        /// Eased fade, for panels that appear and disappear.
        ///
        /// Nothing on screen should pop into existence. A tenth of a second of fade is the cheapest
        /// polish there is and the difference is out of all proportion to the code.
        /// </summary>
        public static float Ease(float t) => t <= 0f ? 0f : t >= 1f ? 1f : t * t * (3f - 2f * t);
    }
}
