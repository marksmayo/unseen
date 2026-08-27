using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Items;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// Minimal diegetic HUD drawn with IMGUI so it needs no art: how hidden you are, how hurt you
    /// are, what you just heard and roughly where from, and which zone your guard is covering.
    ///
    /// Sound pings intentionally lose fidelity with occlusion - a muffled footstep is a wide, faint
    /// smear on the ring rather than a precise marker, which is the whole point of the audio model.
    /// </summary>
    public sealed class StealthHud : MonoBehaviour
    {
        private struct Ping
        {
            public float3 Direction;
            public float Intensity;
            public float Occlusion;
            public SoundKind Kind;
            public float ExpiresAt;
        }

        public ClientNetworkView View;
        public PlayerInputSource Input;

        [Tooltip("How long a sound ping stays on the ring.")]
        public float PingLifetime = 2.2f;

        public bool ShowDebug;

        private readonly List<Ping> _pings = new List<Ping>(24);
        private SnapshotData _snapshot;
        private GUIStyle _label;
        private GUIStyle _small;
        private Texture2D _white;

        private void Awake()
        {
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
        }

        // Subscription happens in Start, not OnEnable: the bootstrap assigns View immediately after
        // AddComponent, which is after OnEnable has already run.
        private void Start()
        {
            if (View != null) View.SnapshotApplied += OnSnapshot;
        }

        private void OnDestroy()
        {
            if (View != null) View.SnapshotApplied -= OnSnapshot;
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F3)) ShowDebug = !ShowDebug;

            for (int i = _pings.Count - 1; i >= 0; i--)
                if (Time.time > _pings[i].ExpiresAt)
                    _pings.RemoveAt(i);
        }

        private void OnSnapshot(SnapshotData snapshot)
        {
            _snapshot = snapshot;

            for (int i = 0; i < snapshot.Sounds.Count; i++)
            {
                HeardSound s = snapshot.Sounds[i];
                _pings.Add(new Ping
                {
                    Direction = s.Direction,
                    Intensity = s.Intensity,
                    Occlusion = s.Occlusion,
                    Kind = s.Kind,
                    ExpiresAt = Time.time + PingLifetime * (0.5f + s.Intensity)
                });
            }
        }

        private void OnGUI()
        {
            if (!GameSettings.Current.ShowHud) return;

            EnsureStyles();

            DrawStealthMeter();
            DrawHealth();
            DrawMatchState();
            DrawGuardZone();
            DrawEliminations();
            DrawResults();
            DrawCrosshair();
            DrawPrompts();
            DrawUtilityBar();
            DrawPingRing();

            if (ShowDebug) DrawDebug();
        }

        private void EnsureStyles()
        {
            if (_label != null) return;

            _label = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        }

        private void DrawStealthMeter()
        {
            float hidden = _snapshot?.SelfStealth ?? 0f;
            var area = new Rect(24f, Screen.height - 74f, 220f, 18f);

            Fill(area, new Color(0f, 0f, 0f, 0.55f));
            Fill(new Rect(area.x, area.y, area.width * hidden, area.height),
                Color.Lerp(new Color(0.85f, 0.7f, 0.25f), new Color(0.2f, 0.35f, 0.7f), hidden));

            GUI.Label(new Rect(area.x, area.y - 20f, 260f, 20f),
                $"hidden {(hidden * 100f):0}%", _label);
        }

        private void DrawHealth()
        {
            float health = _snapshot?.SelfHealth ?? 1f;
            var area = new Rect(24f, Screen.height - 44f, 220f, 12f);

            Fill(area, new Color(0f, 0f, 0f, 0.55f));
            Fill(new Rect(area.x, area.y, area.width * health, area.height),
                Color.Lerp(new Color(0.75f, 0.15f, 0.15f), new Color(0.55f, 0.75f, 0.4f), health));
        }

        private void DrawMatchState()
        {
            if (_snapshot == null) return;

            // Below the minimap, which occupies the top-right corner.
            const float top = 236f;
            string phase = ((BattleRoyale.MatchPhase)_snapshot.MatchPhase).ToString();
            GUI.Label(new Rect(Screen.width - 230f, top, 220f, 22f),
                $"{phase}   alive {_snapshot.AliveCount}", _label);
            GUI.Label(new Rect(Screen.width - 230f, top + 22f, 220f, 22f),
                $"mist stage {_snapshot.ZoneStage}   r {(_snapshot.ZoneRadius):0} m", _small);
        }

        private void DrawGuardZone()
        {
            if (Input == null || !Input.Current.Guard) return;

            string zone = Input.Current.Zone.ToString().ToUpperInvariant();
            var rect = new Rect(Screen.width * 0.5f - 60f, Screen.height * 0.5f + 60f, 120f, 24f);
            Fill(rect, new Color(0f, 0f, 0f, 0.4f));
            GUI.Label(rect, $"  guard: {zone}", _label);
        }

        private struct Elimination
        {
            public string Text;
            public bool Involved;
            public float ExpiresAt;
        }

        private readonly List<Elimination> _eliminations = new List<Elimination>(8);
        private string _ownDeath;

        /// <summary>Name of the agent being spectated, or null while alive. Set by the bootstrap.</summary>
        public string Spectating;

        /// <summary>
        /// Records a kill for the feed. Called by the bootstrap from the server's own death event,
        /// so it reports every elimination in the match rather than only the ones in earshot -
        /// which is what tells you how many are left and how fast the lobby is emptying.
        /// </summary>
        /// <summary>Clears the death banner and the feed at the start of a match.</summary>
        public void NoteMatchStarted()
        {
            _ownDeath = null;
            Spectating = null;
            _eliminations.Clear();
        }

        public void NoteElimination(Entities.AgentEntity victim, Entities.AgentEntity killer)
        {
            if (victim == null) return;

            bool selfVictim = _snapshot != null && victim.Id == _snapshot.SelfId;
            bool selfKiller = killer != null && _snapshot != null && killer.Id == _snapshot.SelfId;

            string line;
            if (selfVictim)
            {
                line = killer != null && killer != victim
                    ? $"eliminated by {killer.DisplayName}"
                    : "eliminated";
                _ownDeath = $"#{victim.Placement}   {line}";
            }
            else if (selfKiller)
            {
                line = $"you eliminated {victim.DisplayName}";
            }
            else if (killer != null && killer != victim)
            {
                line = $"{killer.DisplayName} eliminated {victim.DisplayName}";
            }
            else
            {
                line = $"{victim.DisplayName} eliminated";
            }

            _eliminations.Add(new Elimination
            {
                Text = line,
                Involved = selfVictim || selfKiller,
                ExpiresAt = Time.time + (selfVictim || selfKiller ? 8f : 5f)
            });

            // Never let the feed grow without bound: a 64-player match produces 63 of these.
            while (_eliminations.Count > 5) _eliminations.RemoveAt(0);
        }

        private void DrawEliminations()
        {
            for (int i = _eliminations.Count - 1; i >= 0; i--)
                if (Time.time > _eliminations[i].ExpiresAt)
                    _eliminations.RemoveAt(i);

            float y = 288f;
            for (int i = 0; i < _eliminations.Count; i++)
            {
                Elimination e = _eliminations[i];
                float life = Mathf.Clamp01(e.ExpiresAt - Time.time);
                var rect = new Rect(Screen.width - 330f, y + i * 24f, 320f, 22f);

                Fill(rect, new Color(0f, 0f, 0f, 0.4f * Mathf.Min(1f, life * 2f)));
                GUI.color = e.Involved
                    ? new Color(1f, 0.86f, 0.5f, Mathf.Min(1f, life * 2f))
                    : new Color(0.85f, 0.85f, 0.85f, Mathf.Min(1f, life * 2f));
                GUI.Label(new Rect(rect.x + 8f, rect.y, rect.width - 12f, rect.height), e.Text, _small);
                GUI.color = Color.white;
            }

            if (_ownDeath == null) return;

            // Your own death stays up. It is the end of your match, not a feed item.
            var banner = new Rect(Screen.width * 0.5f - 210f, Screen.height * 0.32f, 420f, 90f);
            Fill(banner, new Color(0f, 0f, 0f, 0.62f));
            GUI.Label(new Rect(banner.x + 20f, banner.y + 10f, banner.width - 40f, 28f),
                "ELIMINATED", _label);
            GUI.Label(new Rect(banner.x + 20f, banner.y + 36f, banner.width - 40f, 22f),
                _ownDeath, _small);

            // Tell them the match is still running and how to watch it. Without this the screen
            // after death is a corpse and no explanation.
            string watching = Spectating != null
                ? $"spectating {Spectating}   -   jump key cycles"
                : "waiting for the next match";
            GUI.Label(new Rect(banner.x + 20f, banner.y + 58f, banner.width - 40f, 22f),
                watching, _small);
        }

        /// <summary>
        /// The end of a match: how you did, and how long until the next one.
        ///
        /// Driven entirely from the snapshot rather than a server event. MatchDirector fires
        /// MatchEnded exactly once, and a client that had dropped that packet - or that connected
        /// during the post-match window - would never find out it had won. Result state is cheap
        /// enough to send with every snapshot, so it is.
        /// </summary>
        /// <summary>
        /// The end-of-match table: where everyone finished, how many they took with them, and how
        /// they went out.
        ///
        /// Sixty-four rows will not fit on a screen and would not be worth reading if they did, so
        /// the table shows the top of the board and then guarantees the local player a line -
        /// finishing forty-first is the result that most needs reporting, and it is exactly the one
        /// a fixed top-ten would drop.
        /// </summary>
        private void DrawResults()
        {
            if (_snapshot == null) return;
            if ((BattleRoyale.MatchPhase)_snapshot.MatchPhase != BattleRoyale.MatchPhase.PostMatch) return;

            bool won = _snapshot.Winner.IsValid && _snapshot.Winner == _snapshot.SelfId;
            var accent = won ? new Color(1f, 0.86f, 0.45f) : new Color(0.78f, 0.79f, 0.84f);

            BuildBoard();

            const float rowHeight = 21f;
            float width = 620f;
            float header = 92f;
            float footer = 54f;
            float height = header + _board.Count * rowHeight + footer;

            var panel = new Rect(Screen.width * 0.5f - width * 0.5f,
                Mathf.Max(24f, Screen.height * 0.5f - height * 0.5f), width, height);

            Fill(panel, new Color(0f, 0f, 0f, 0.82f));
            Fill(new Rect(panel.x, panel.y, panel.width, 3f), accent);

            float left = panel.x + 22f;
            float inner = panel.width - 44f;

            GUI.color = accent;
            GUI.Label(new Rect(left, panel.y + 14f, inner, 32f),
                won ? "THE LAST UNSEEN" : "MATCH OVER", _label);
            GUI.color = Color.white;

            // The player's own line, spelled out above the table. It is the one fact they came to
            // this screen for and it should not have to be found in a list.
            string placement = _snapshot.SelfPlacement > 0
                ? $"#{_snapshot.SelfPlacement}"
                : won ? "#1" : "still standing";

            GUI.color = new Color(0.72f, 0.74f, 0.8f);
            GUI.Label(new Rect(left, panel.y + 40f, inner, 20f),
                $"you finished {placement} with {_snapshot.SelfKills} " +
                (_snapshot.SelfKills == 1 ? "elimination" : "eliminations"), _small);
            GUI.color = Color.white;

            // Column heads.
            float y = panel.y + 66f;
            GUI.color = new Color(0.55f, 0.57f, 0.63f);
            GUI.Label(new Rect(left, y, 40f, 18f), "#", _small);
            GUI.Label(new Rect(left + 44f, y, 180f, 18f), "ninja", _small);
            GUI.Label(new Rect(left + 232f, y, 60f, 18f), "kills", _small);
            GUI.Label(new Rect(left + 300f, y, inner - 300f, 18f), "fate", _small);
            GUI.color = Color.white;

            Fill(new Rect(left, y + 19f, inner, 1f), new Color(1f, 1f, 1f, 0.14f));

            y = panel.y + header;

            for (int i = 0; i < _board.Count; i++)
            {
                Standing row = _board[i];
                bool self = row.Id == _snapshot.SelfId;
                bool first = row.Placement == 1;

                var line = new Rect(left - 6f, y, inner + 12f, rowHeight);

                if (self) Fill(line, new Color(0.36f, 0.5f, 0.72f, 0.34f));
                else if (i % 2 == 1) Fill(line, new Color(1f, 1f, 1f, 0.035f));

                GUI.color = first ? new Color(1f, 0.86f, 0.45f)
                    : self ? Color.white
                    : new Color(0.82f, 0.83f, 0.87f);

                GUI.Label(new Rect(left, y + 2f, 40f, 18f),
                    row.Placement > 0 ? $"{row.Placement}" : "-", _small);

                GUI.Label(new Rect(left + 44f, y + 2f, 180f, 18f),
                    string.IsNullOrEmpty(row.Name) ? "ninja" : row.Name, _small);

                GUI.Label(new Rect(left + 232f, y + 2f, 60f, 18f), $"{row.Kills}", _small);

                GUI.color = first ? new Color(1f, 0.86f, 0.45f) : new Color(0.66f, 0.68f, 0.73f);
                GUI.Label(new Rect(left + 300f, y + 2f, inner - 300f, 18f), FateText(row), _small);

                GUI.color = Color.white;
                y += rowHeight;
            }

            float countdown = Mathf.Max(0f, _snapshot.PhaseSecondsRemaining);
            GUI.color = new Color(0.72f, 0.74f, 0.8f);
            GUI.Label(new Rect(left, panel.yMax - 44f, inner, 20f),
                countdown > 0f ? $"next match in {countdown:0}s" : "next match starting", _small);
            GUI.color = Color.white;

            var bar = new Rect(left, panel.yMax - 20f, inner, 5f);
            Fill(bar, new Color(1f, 1f, 1f, 0.12f));

            float span = Mathf.Max(1f, PostMatchSpan);
            Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(1f - countdown / span), bar.height),
                accent);
        }

        private readonly List<Standing> _board = new List<Standing>(24);
        private readonly List<Standing> _sorted = new List<Standing>(64);

        [Tooltip("Rows of the results table shown from the top of the board, before the local " +
                 "player's own row is guaranteed a place.")]
        public int ResultRows = 10;

        /// <summary>
        /// Orders the roster and picks the rows worth showing. Rebuilt each frame from the snapshot
        /// because IMGUI has nowhere else to keep it, and both lists are reused so a screen that is
        /// up for twelve seconds does not allocate seven hundred times.
        /// </summary>
        private void BuildBoard()
        {
            _sorted.Clear();
            _sorted.AddRange(_snapshot.Standings);

            // Placement ascending, with anyone still standing treated as the winner - which is what
            // they are, since the match only ends when one is left. Kills break ties so a lobby cut
            // short still reads sensibly rather than in slot order.
            _sorted.Sort((a, b) =>
            {
                int pa = a.Placement == 0 ? 1 : a.Placement;
                int pb = b.Placement == 0 ? 1 : b.Placement;
                if (pa != pb) return pa.CompareTo(pb);
                return b.Kills.CompareTo(a.Kills);
            });

            _board.Clear();

            int take = Mathf.Clamp(ResultRows, 3, 24);
            bool selfShown = false;

            for (int i = 0; i < _sorted.Count && _board.Count < take; i++)
            {
                _board.Add(_sorted[i]);
                if (_sorted[i].Id == _snapshot.SelfId) selfShown = true;
            }

            if (selfShown) return;

            // The local player's own row, appended below the cut. Placing forty-first is the result
            // that most needs reporting and is exactly the one a fixed top-ten would drop.
            for (int i = take; i < _sorted.Count; i++)
            {
                if (_sorted[i].Id != _snapshot.SelfId) continue;
                _board.Add(_sorted[i]);
                return;
            }
        }

        /// <summary>
        /// How somebody went out, in words. Named for the thing that did it rather than the damage
        /// type, because "SpiritForest" is a code identifier and "taken by the forest" is what
        /// happened.
        /// </summary>
        private string FateText(in Standing row)
        {
            if (!row.Died) return row.Placement == 1 ? "survived" : "still standing";

            string by = null;
            if (row.Killer.IsValid)
            {
                by = NameOf(row.Killer);
                if (row.Killer == _snapshot.SelfId) by = "you";
            }

            switch ((DamageKind)row.Cause)
            {
                case DamageKind.Takedown:
                    return by != null ? $"throat cut by {by}" : "cut down from behind";
                case DamageKind.Melee:
                    return by != null ? $"cut down by {by}" : "cut down";
                case DamageKind.Thrown:
                    return by != null ? $"shuriken from {by}" : "took a blade";
                case DamageKind.Fall:
                    return "fell";
                case DamageKind.Mist:
                    return "lost to the mist";
                case DamageKind.SpiritForest:
                    return "taken by the forest";
                case DamageKind.Drowning:
                    return "drowned";
                default:
                    return by != null ? $"eliminated by {by}" : "eliminated";
            }
        }

        /// <summary>A name for an id, out of the table itself - the only roster the client has.</summary>
        private string NameOf(AgentId id)
        {
            for (int i = 0; i < _sorted.Count; i++)
                if (_sorted[i].Id == id)
                    return string.IsNullOrEmpty(_sorted[i].Name) ? "a ninja" : _sorted[i].Name;

            return "a ninja";
        }

        [Tooltip("Expected post-match window, used only to scale the countdown bar.")]
        public float PostMatchSpan = 12f;

        /// <summary>A dot, so aiming the grapple and the guard zone has something to aim with.</summary>
        private void DrawCrosshair()
        {
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            bool grapple = HasPrompt(SelfPrompt.Grapple);

            Fill(new Rect(centre.x - 2f, centre.y - 2f, 4f, 4f),
                grapple ? new Color(0.6f, 0.85f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.5f));

            if (!grapple) return;

            // Corner ticks when an anchor is in reach. Without this the hook is invisible: you
            // cannot tell a bad angle from a broken button.
            const float gap = 9f, len = 6f;
            var tint = new Color(0.6f, 0.85f, 1f, 0.9f);
            Fill(new Rect(centre.x - gap - len, centre.y - 1f, len, 2f), tint);
            Fill(new Rect(centre.x + gap, centre.y - 1f, len, 2f), tint);
            Fill(new Rect(centre.x - 1f, centre.y - gap - len, 2f, len), tint);
            Fill(new Rect(centre.x - 1f, centre.y + gap, 2f, len), tint);
        }

        /// <summary>Context prompts for whatever is actually in reach, straight from the server.</summary>
        private void DrawPrompts()
        {
            string action = null;
            if (HasPrompt(SelfPrompt.Container)) action = "E   loot chest";
            else if (HasPrompt(SelfPrompt.Shoji)) action = "E   slice shoji";
            else if (HasPrompt(SelfPrompt.Lantern)) action = "E   douse lantern";

            var lines = new List<string>(2);
            if (action != null) lines.Add(action);
            if (HasPrompt(SelfPrompt.Grapple)) lines.Add("F   grapple");
            if (lines.Count == 0) return;

            float y = Screen.height * 0.5f + 96f;
            for (int i = 0; i < lines.Count; i++)
            {
                var rect = new Rect(Screen.width * 0.5f - 90f, y + i * 26f, 180f, 22f);
                Fill(rect, new Color(0f, 0f, 0f, 0.45f));
                GUI.Label(new Rect(rect.x + 10f, rect.y, rect.width, rect.height), lines[i], _label);
            }
        }

        /// <summary>The three utility slots, so 1/2/3 say what they will do before you press them.</summary>
        private void DrawUtilityBar()
        {
            if (_snapshot == null) return;

            const float slotWidth = 92f, slotHeight = 26f, spacing = 6f;
            float total = slotWidth * 3f + spacing * 2f;
            float x = Screen.width * 0.5f - total * 0.5f;
            float y = Screen.height - 40f;

            for (int i = 0; i < 3; i++)
            {
                var rect = new Rect(x + i * (slotWidth + spacing), y, slotWidth, slotHeight);
                byte effect = _snapshot.SelfUtility[i];
                bool filled = effect != 0;

                Fill(rect, filled ? new Color(0.08f, 0.1f, 0.16f, 0.8f) : new Color(0f, 0f, 0f, 0.35f));
                GUI.Label(new Rect(rect.x + 6f, rect.y + 3f, 16f, 20f), (i + 1).ToString(),
                    filled ? _label : _small);
                GUI.Label(new Rect(rect.x + 22f, rect.y + 5f, slotWidth - 26f, 20f),
                    filled ? UtilityName((UtilityEffect)effect) : "-", _small);
            }
        }

        private static string UtilityName(UtilityEffect effect)
        {
            switch (effect)
            {
                case UtilityEffect.SmokeBomb: return "smoke";
                case UtilityEffect.Noisemaker: return "noisemaker";
                case UtilityEffect.NightVisionElixir: return "night eyes";
                default: return "-";
            }
        }

        private bool HasPrompt(SelfPrompt prompt) =>
            _snapshot != null && ((SelfPrompt)_snapshot.SelfPrompts & prompt) != 0;

        private void DrawPingRing()
        {
            if (_pings.Count == 0 || Input == null) return;

            Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            const float radius = 150f;

            for (int i = 0; i < _pings.Count; i++)
            {
                Ping ping = _pings[i];

                // Rotate the world direction into view space so the ring is camera relative.
                float pingYaw = UnseenMath.ForwardToYaw(ping.Direction);
                float relative = UnseenMath.YawDelta(Input.Yaw, pingYaw) * UnseenMath.Deg2Rad;

                float life = Mathf.Clamp01((ping.ExpiresAt - Time.time) / PingLifetime);
                float alpha = life * Mathf.Clamp01(ping.Intensity + 0.2f);

                // Muffled sounds smear: the marker gets wider and dimmer with occlusion.
                float width = Mathf.Lerp(10f, 46f, ping.Occlusion);
                var rect = new Rect(
                    centre.x + Mathf.Sin(relative) * radius - width * 0.5f,
                    centre.y - Mathf.Cos(relative) * radius - 3f,
                    width, 6f);

                Fill(rect, new Color(1f, 0.92f, 0.7f, alpha * 0.8f));

                if (ping.Occlusion < 0.3f && ping.Intensity > 0.5f)
                    GUI.Label(new Rect(rect.x - 12f, rect.y - 18f, 120f, 18f), ping.Kind.ToString(), _small);
            }
        }

        private float _frameMs;

        private void DrawDebug()
        {
            var rect = new Rect(24f, 18f, 420f, 168f);
            Fill(rect, new Color(0f, 0f, 0f, 0.55f));

            _frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.06f);

            string body =
                $"frame {_frameMs:0.0} ms ({(_frameMs > 0.01f ? 1000f / _frameMs : 0f):0} fps)\n" +
                $"snapshots {View?.SnapshotsReceived ?? 0}   proxies {View?.ProxyCount ?? 0}\n" +
                $"bytes in {(View?.BytesReceived ?? 0) / 1024} KiB\n" +
                $"tick {_snapshot?.Tick ?? 0}   visible {_snapshot?.Entities.Count ?? 0}\n" +
                $"self {_snapshot?.SelfId.ToString() ?? "-"}   " +
                $"loco {(LocomotionState)(_snapshot?.SelfLocomotion ?? 0)}\n" +
                $"flags {(AgentFlags)(_snapshot?.SelfFlags ?? 0)}";

            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f), body, _small);
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
