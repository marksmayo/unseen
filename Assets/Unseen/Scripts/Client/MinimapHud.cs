using UnityEngine;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// A corner map, so a town of two hundred and fifty identical compounds is navigable.
    ///
    /// North-up rather than heading-up on purpose. A rotating map is easier to follow for the next
    /// ten seconds and useless for building a mental picture of a place; this game asks the player
    /// to remember where the keep was, which side of the river they are on, and which way the mist
    /// is pushing them, and all three of those want a stable north.
    ///
    /// It draws the layout and nothing living. No enemy markers, not even for people you can
    /// currently see - the whole information model of the game is that you know what you have
    /// earned by looking and listening, and a minimap that quietly aggregates that into a
    /// god's-eye view would undo the stealth design from the UI layer. Sounds you have already been
    /// told about do appear, because you were already told.
    /// </summary>
    public sealed class MinimapHud : MonoBehaviour
    {
        public ClientNetworkView View;
        public PlayerInputSource Input;

        [Tooltip("Size of the map panel in pixels.")]
        public float Size = 190f;

        [Tooltip("Margin from the screen corner.")]
        public float Margin = 16f;

        [Tooltip("Metres from edge to edge of the panel when zoomed in.")]
        public float NearSpan = 190f;

        [Tooltip("Toggles between the local view and the whole town.")]
        public KeyCode ExpandKey = KeyCode.M;

        public bool ShowWholeMap;

        private MapSketch _sketch;
        private MapDescriptor _map;
        private SnapshotData _snapshot;
        private Texture2D _white;
        private GUIStyle _tiny;

        private static readonly Color Backdrop = new Color(0.05f, 0.06f, 0.09f, 0.78f);
        private static readonly Color BlockColour = new Color(0.30f, 0.31f, 0.35f, 0.95f);
        private static readonly Color KeepColour = new Color(0.62f, 0.52f, 0.30f, 1f);
        private static readonly Color PagodaColour = new Color(0.48f, 0.38f, 0.52f, 1f);
        private static readonly Color WaterColour = new Color(0.16f, 0.30f, 0.42f, 1f);
        private static readonly Color BridgeColour = new Color(0.45f, 0.36f, 0.24f, 1f);
        private static readonly Color PlazaColour = new Color(0.19f, 0.20f, 0.23f, 0.9f);
        private static readonly Color MistColour = new Color(0.62f, 0.40f, 0.85f, 0.9f);
        private static readonly Color SelfColour = new Color(0.95f, 0.93f, 0.80f, 1f);

        private void Awake()
        {
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
        }

        private void Start()
        {
            if (View != null) View.SnapshotApplied += OnSnapshot;
        }

        private void OnDestroy()
        {
            if (View != null) View.SnapshotApplied -= OnSnapshot;
        }

        private void OnSnapshot(SnapshotData snapshot)
        {
            _snapshot = snapshot;
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(ExpandKey)) ShowWholeMap = !ShowWholeMap;
        }

        private void OnGUI()
        {
            if (!GameSettings.Current.ShowHud || _snapshot == null) return;

            if (_sketch == null) _sketch = MapSketch.Find();
            if (_map == null) _map = MapDescriptor.Find();
            if (_sketch == null) return;

            EnsureStyles();

            float span = ShowWholeMap ? _sketch.Extent * 2.2f : NearSpan;
            float pixelsPerMetre = Size / span;

            var panel = new Rect(Screen.width - Size - Margin, Margin, Size, Size);
            Vector2 centre = new Vector2(panel.x + Size * 0.5f, panel.y + Size * 0.5f);

            // The view follows the player when zoomed in, and sits on the map centre when zoomed
            // out - a whole-town view that slides around is harder to read, not easier.
            Vector2 focus = ShowWholeMap
                ? new Vector2(_map != null ? _map.Center.x : 0f, _map != null ? _map.Center.z : 0f)
                : new Vector2(_snapshot.SelfPosition.x, _snapshot.SelfPosition.z);

            Fill(panel, Backdrop);

            GUI.BeginClip(panel);
            Vector2 local = centre - new Vector2(panel.x, panel.y);

            DrawLandmarks(local, focus, pixelsPerMetre);
            DrawMist(local, focus, pixelsPerMetre);
            DrawSounds(local, pixelsPerMetre);
            DrawSelf(local, focus, pixelsPerMetre);
            GUI.EndClip();

            // Border last, over the clipped contents.
            Outline(panel, new Color(0f, 0f, 0f, 0.85f));

            GUI.Label(new Rect(panel.x + 6f, panel.yMax + 2f, Size, 18f),
                ShowWholeMap ? $"town   {ExpandKey} to zoom in" : $"{span:0} m   {ExpandKey} for the town",
                _tiny);
        }

        private void DrawLandmarks(Vector2 origin, Vector2 focus, float scale)
        {
            for (int i = 0; i < _sketch.Landmarks.Count; i++)
            {
                MapSketch.Landmark mark = _sketch.Landmarks[i];

                // North is up, so world +Z maps to screen -Y.
                Vector2 offset = (mark.Center - focus) * scale;
                Vector2 size = mark.Extents * 2f * scale;
                if (size.x < 1.2f) size.x = 1.2f;
                if (size.y < 1.2f) size.y = 1.2f;

                var rect = new Rect(
                    origin.x + offset.x - size.x * 0.5f,
                    origin.y - offset.y - size.y * 0.5f,
                    size.x, size.y);

                if (rect.xMax < 0f || rect.yMax < 0f || rect.x > Size || rect.y > Size) continue;

                Fill(rect, ColourOf(mark.Kind));
            }
        }

        private static Color ColourOf(MapSketch.Feature kind)
        {
            switch (kind)
            {
                case MapSketch.Feature.Keep: return KeepColour;
                case MapSketch.Feature.Pagoda: return PagodaColour;
                case MapSketch.Feature.Water: return WaterColour;
                case MapSketch.Feature.Bridge: return BridgeColour;
                case MapSketch.Feature.Plaza: return PlazaColour;
                default: return BlockColour;
            }
        }

        /// <summary>The mist circle, drawn as a ring of ticks. The thing you are running from.</summary>
        private void DrawMist(Vector2 origin, Vector2 focus, float scale)
        {
            if (_snapshot.ZoneRadius <= 0f) return;

            var zoneCentre = new Vector2(_snapshot.ZoneCenter.x, _snapshot.ZoneCenter.z);
            Vector2 offset = (zoneCentre - focus) * scale;
            float radius = _snapshot.ZoneRadius * scale;

            const int segments = 48;
            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                var point = new Vector2(
                    origin.x + offset.x + Mathf.Sin(angle) * radius,
                    origin.y - offset.y - Mathf.Cos(angle) * radius);

                if (point.x < 0f || point.y < 0f || point.x > Size || point.y > Size) continue;
                Fill(new Rect(point.x - 1.5f, point.y - 1.5f, 3f, 3f), MistColour);
            }
        }

        /// <summary>Sounds already reported to this player, fading as their pings do.</summary>
        private void DrawSounds(Vector2 origin, float scale)
        {
            for (int i = 0; i < _snapshot.Sounds.Count; i++)
            {
                HeardSound sound = _snapshot.Sounds[i];

                // Apparent position, not the true one: the map must agree with the ear.
                var at = new Vector2(sound.ApparentPosition.x, sound.ApparentPosition.z);
                var self = new Vector2(_snapshot.SelfPosition.x, _snapshot.SelfPosition.z);
                Vector2 offset = (at - self) * scale;

                float size = Mathf.Lerp(3f, 7f, sound.Occlusion);
                var rect = new Rect(origin.x + offset.x - size * 0.5f,
                    origin.y - offset.y - size * 0.5f, size, size);

                if (rect.x < 0f || rect.y < 0f || rect.xMax > Size || rect.yMax > Size) continue;
                Fill(rect, new Color(1f, 0.92f, 0.7f, 0.35f + 0.5f * sound.Intensity));
            }
        }

        private void DrawSelf(Vector2 origin, Vector2 focus, float scale)
        {
            var self = new Vector2(_snapshot.SelfPosition.x, _snapshot.SelfPosition.z);
            Vector2 offset = (self - focus) * scale;
            var at = new Vector2(origin.x + offset.x, origin.y - offset.y);

            Fill(new Rect(at.x - 2.5f, at.y - 2.5f, 5f, 5f), SelfColour);

            // A short spur for facing, which is the whole reason a north-up map still works.
            float yaw = (Input != null ? Input.Yaw : _snapshot.SelfYaw) * Mathf.Deg2Rad;
            for (int i = 2; i <= 9; i++)
            {
                var tip = new Vector2(at.x + Mathf.Sin(yaw) * i, at.y - Mathf.Cos(yaw) * i);
                if (tip.x < 0f || tip.y < 0f || tip.x > Size || tip.y > Size) break;
                Fill(new Rect(tip.x - 1f, tip.y - 1f, 2f, 2f), SelfColour);
            }
        }

        private void EnsureStyles()
        {
            if (_tiny != null) return;
            _tiny = new GUIStyle(GUI.skin.label) { fontSize = 10 };
        }

        private void Outline(Rect rect, Color colour)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1f), colour);
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), colour);
            Fill(new Rect(rect.x, rect.y, 1f, rect.height), colour);
            Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), colour);
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
