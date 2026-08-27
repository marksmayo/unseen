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

            DrawVitals();
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

        /// <summary>
        /// How hidden you are and how hurt you are, as one block in the bottom-left corner.
        ///
        /// These were two loose bars sitting on the screen with a label floating above one of them.
        /// Grouping them on a panel is most of the difference: a reading with an edge around it is
        /// an instrument, and the same reading without one is a stray rectangle.
        ///
        /// Hidden is the larger of the two on purpose. It is the number this game is about, and it
        /// is the one a player should be able to read out of the corner of their eye.
        /// </summary>
        private void DrawVitals()
        {
            float hidden = _snapshot?.SelfStealth ?? 0f;
            float health = _snapshot?.SelfHealth ?? 1f;

            var panel = new Rect(22f, Screen.height - 104f, 252f, 82f);
            UnseenUi.Panel(panel);

            float x = panel.x + 16f;
            float w = panel.width - 32f;

            // Cool when you are hidden, warm when you are not: the colour says as much as the
            // length does, and at a glance it says it faster.
            Color tint = Color.Lerp(UnseenUi.Gold, UnseenUi.Cool, hidden);

            UnseenUi.Say(new Rect(x, panel.y + 10f, w, 18f), "HIDDEN", UnseenUi.Section,
                UnseenUi.Faint);

            UnseenUi.Say(new Rect(x, panel.y + 8f, w, 20f), $"{hidden * 100f:0}%",
                UnseenUi.Number, tint);

            UnseenUi.Meter(new Rect(x, panel.y + 32f, w, 8f), hidden, tint);

            UnseenUi.Say(new Rect(x, panel.y + 48f, w, 16f), "BODY", UnseenUi.Section,
                UnseenUi.Faint);

            UnseenUi.Meter(new Rect(x, panel.y + 64f, w, 6f), health,
                Color.Lerp(UnseenUi.Blood, new Color(0.52f, 0.72f, 0.44f), health));
        }

        /// <summary>
        /// The phase, the survivor count, and where the mist has got to.
        ///
        /// Sits under the minimap in the top-right corner. The alive count is the largest figure
        /// here because it is the one that changes the way a match feels - forty alive and four
        /// alive are different games - and it used to be the same size as the words around it.
        /// </summary>
        private void DrawMatchState()
        {
            if (_snapshot == null) return;

            var panel = new Rect(Screen.width - 246f, 232f, 224f, 74f);
            UnseenUi.Panel(panel);

            float x = panel.x + 16f;
            float w = panel.width - 32f;

            string phase = ((BattleRoyale.MatchPhase)_snapshot.MatchPhase).ToString();

            UnseenUi.Say(new Rect(x, panel.y + 10f, w, 18f), phase.ToUpperInvariant(),
                UnseenUi.Section, UnseenUi.Accent);

            UnseenUi.Say(new Rect(x, panel.y + 26f, 60f, 28f), $"{_snapshot.AliveCount}",
                UnseenUi.Title, UnseenUi.Text);

            UnseenUi.Say(new Rect(x + 34f, panel.y + 30f, w - 34f, 20f), "still unseen",
                UnseenUi.Caption, UnseenUi.Muted);

            UnseenUi.Fill(new Rect(x, panel.y + 56f, w, 1f), new Color(1f, 1f, 1f, 0.07f));

            UnseenUi.Say(new Rect(x, panel.y + 56f, w, 18f),
                $"mist {_snapshot.ZoneStage}", UnseenUi.Caption, UnseenUi.Faint);

            UnseenUi.Say(new Rect(x, panel.y + 56f, w, 18f),
                $"{_snapshot.ZoneRadius:0} m", UnseenUi.Number, UnseenUi.Muted);
        }

        private void DrawGuardZone()
        {
            if (Input == null || !Input.Current.Guard) return;

            string zone = Input.Current.Zone.ToString().ToUpperInvariant();
            var rect = new Rect(Screen.width * 0.5f - 58f, Screen.height * 0.5f + 58f, 116f, 26f);

            UnseenUi.Rounded(rect, UnseenUi.Ink, UnseenUi.SmallRadius, UnseenUi.Edge, 1f);

            var centred = new GUIStyle(UnseenUi.Section) { alignment = TextAnchor.MiddleCenter };
            UnseenUi.Say(rect, $"GUARD  {zone}", centred, UnseenUi.Cool);
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

            float y = 318f;
            for (int i = 0; i < _eliminations.Count; i++)
            {
                Elimination e = _eliminations[i];

                // Fading out over the last half second rather than blinking off.
                float life = Mathf.Clamp01((e.ExpiresAt - Time.time) * 2f);

                var rect = new Rect(Screen.width - 336f, y + i * 26f, 314f, 24f);

                UnseenUi.Rounded(rect, UnseenUi.Ink * new Color(1f, 1f, 1f, life),
                    UnseenUi.SmallRadius);

                // A stripe down the left in the accent when you were part of it. The feed is
                // mostly other people's business and the ones that are yours should not need
                // reading to be noticed.
                if (e.Involved)
                    UnseenUi.Rounded(new Rect(rect.x, rect.y + 4f, 2f, rect.height - 8f),
                        UnseenUi.Gold * new Color(1f, 1f, 1f, life), 1f);

                UnseenUi.Say(new Rect(rect.x + 12f, rect.y, rect.width - 20f, rect.height),
                    e.Text, UnseenUi.Label, e.Involved ? UnseenUi.Gold : UnseenUi.Muted, life);
            }

            if (_ownDeath == null) return;

            // Your own death stays up. It is the end of your match, not a feed item.
            var banner = new Rect(Screen.width * 0.5f - 230f, Screen.height * 0.30f, 460f, 104f);

            UnseenUi.Panel(banner);
            UnseenUi.Accented(banner, UnseenUi.Blood);

            UnseenUi.Say(new Rect(banner.x + 24f, banner.y + 16f, banner.width - 48f, 32f),
                "ELIMINATED", UnseenUi.Display, UnseenUi.Text);

            UnseenUi.Say(new Rect(banner.x + 24f, banner.y + 50f, banner.width - 48f, 22f),
                _ownDeath, UnseenUi.Body, UnseenUi.Muted);

            // Tell them the match is still running and how to watch it. Without this the screen
            // after death is a corpse and no explanation.
            string watching = Spectating != null
                ? $"spectating {Spectating}  ·  jump cycles"
                : "waiting for the next match";

            UnseenUi.Fill(new Rect(banner.x + 24f, banner.y + 78f, banner.width - 48f, 1f),
                new Color(1f, 1f, 1f, 0.07f));

            UnseenUi.Say(new Rect(banner.x + 24f, banner.y + 78f, banner.width - 48f, 22f),
                watching, UnseenUi.Caption, UnseenUi.Faint);
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
            Color accent = won ? UnseenUi.Gold : UnseenUi.Accent;

            BuildBoard();

            const float rowHeight = 26f;
            const float width = 660f;
            const float header = 128f;
            const float footer = 62f;

            float height = header + _board.Count * rowHeight + footer;

            var panel = new Rect(
                Mathf.Round(Screen.width * 0.5f - width * 0.5f),
                Mathf.Round(Mathf.Max(24f, Screen.height * 0.5f - height * 0.5f)),
                width, height);

            UnseenUi.Panel(panel);
            UnseenUi.Accented(panel, accent);

            float left = panel.x + 26f;
            float inner = panel.width - 52f;

            // ---------------------------------------------------------------- the headline
            UnseenUi.Say(new Rect(left, panel.y + 22f, inner, 34f),
                won ? "THE LAST UNSEEN" : "MATCH OVER", UnseenUi.Display, accent);

            // The player's own line, spelled out above the table. It is the one fact they came to
            // this screen for and it should not have to be found in a list.
            string placement = _snapshot.SelfPlacement > 0
                ? $"#{_snapshot.SelfPlacement}"
                : won ? "#1" : "still standing";

            UnseenUi.Say(new Rect(left, panel.y + 58f, inner, 22f),
                $"you finished {placement} with {_snapshot.SelfKills} " +
                (_snapshot.SelfKills == 1 ? "elimination" : "eliminations"),
                UnseenUi.Body, UnseenUi.Muted);

            // ---------------------------------------------------------------- column heads
            float y = panel.y + 96f;

            UnseenUi.Say(new Rect(left, y, 44f, 18f), "#", UnseenUi.Section, UnseenUi.Faint);
            UnseenUi.Say(new Rect(left + 48f, y, 190f, 18f), "NINJA", UnseenUi.Section,
                UnseenUi.Faint);
            UnseenUi.Say(new Rect(left + 238f, y, 60f, 18f), "KILLS", UnseenUi.Number,
                UnseenUi.Faint);
            UnseenUi.Say(new Rect(left + 320f, y, inner - 320f, 18f), "FATE", UnseenUi.Section,
                UnseenUi.Faint);

            UnseenUi.Fill(new Rect(left, y + 20f, inner, 1f), new Color(1f, 1f, 1f, 0.10f));

            // ---------------------------------------------------------------- the table
            y = panel.y + header;

            for (int i = 0; i < _board.Count; i++)
            {
                Standing row = _board[i];
                bool self = row.Id == _snapshot.SelfId;
                bool first = row.Placement == 1;

                var line = new Rect(left - 8f, y, inner + 16f, rowHeight);

                if (self)
                    UnseenUi.Rounded(line, accent * new Color(1f, 1f, 1f, 0.16f),
                        UnseenUi.SmallRadius, accent * new Color(1f, 1f, 1f, 0.45f), 1f);
                else if (i % 2 == 1)
                    UnseenUi.Rounded(line, UnseenUi.Raised, UnseenUi.SmallRadius);

                Color ink = first ? UnseenUi.Gold : self ? UnseenUi.Text : UnseenUi.Muted;

                UnseenUi.Say(new Rect(left, y, 44f, rowHeight),
                    row.Placement > 0 ? $"{row.Placement}" : "-", UnseenUi.Label, ink);

                UnseenUi.Say(new Rect(left + 48f, y, 190f, rowHeight),
                    string.IsNullOrEmpty(row.Name) ? "ninja" : row.Name, UnseenUi.Body, ink);

                UnseenUi.Say(new Rect(left + 238f, y, 60f, rowHeight), $"{row.Kills}",
                    UnseenUi.Number, row.Kills > 0 ? ink : UnseenUi.Faint);

                UnseenUi.Say(new Rect(left + 320f, y, inner - 320f, rowHeight), FateText(row),
                    UnseenUi.Label, first ? UnseenUi.Gold : UnseenUi.Faint);

                y += rowHeight;
            }

            // ---------------------------------------------------------------- the countdown
            float countdown = Mathf.Max(0f, _snapshot.PhaseSecondsRemaining);

            UnseenUi.Fill(new Rect(left, panel.yMax - 50f, inner, 1f),
                new Color(1f, 1f, 1f, 0.07f));

            UnseenUi.Say(new Rect(left, panel.yMax - 44f, inner, 20f),
                countdown > 0f ? $"next match in {countdown:0}s" : "next match starting",
                UnseenUi.Caption, UnseenUi.Muted);

            var bar = new Rect(left, panel.yMax - 22f, inner, 5f);
            float span = Mathf.Max(1f, PostMatchSpan);

            UnseenUi.Meter(bar, Mathf.Clamp01(1f - countdown / span), accent);
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

            // A ring rather than a square pixel, with a dark rim so it survives being over a
            // pale wall as well as a dark street.
            UnseenUi.Rounded(new Rect(centre.x - 3.5f, centre.y - 3.5f, 7f, 7f),
                new Color(0f, 0f, 0f, 0.55f), 3.5f);

            UnseenUi.Rounded(new Rect(centre.x - 2f, centre.y - 2f, 4f, 4f),
                grapple ? UnseenUi.Cool : new Color(1f, 1f, 1f, 0.62f), 2f);

            if (!grapple) return;

            // Corner ticks when an anchor is in reach. Without this the hook is invisible: you
            // cannot tell a bad angle from a broken button.
            const float gap = 10f, len = 7f;
            Color tint = UnseenUi.Cool;

            UnseenUi.Rounded(new Rect(centre.x - gap - len, centre.y - 1f, len, 2f), tint, 1f);
            UnseenUi.Rounded(new Rect(centre.x + gap, centre.y - 1f, len, 2f), tint, 1f);
            UnseenUi.Rounded(new Rect(centre.x - 1f, centre.y - gap - len, 2f, len), tint, 1f);
            UnseenUi.Rounded(new Rect(centre.x - 1f, centre.y + gap, 2f, len), tint, 1f);
        }

        /// <summary>Context prompts for whatever is actually in reach, straight from the server.</summary>
        private void DrawPrompts()
        {
            string action = null;
            if (HasPrompt(SelfPrompt.Container)) action = "loot the chest";
            else if (HasPrompt(SelfPrompt.Shoji)) action = "slice the shoji";
            else if (HasPrompt(SelfPrompt.Lantern)) action = "douse the lantern";

            _prompts.Clear();
            if (action != null) _prompts.Add(("E", action));
            if (HasPrompt(SelfPrompt.Grapple)) _prompts.Add(("F", "grapple"));
            if (_prompts.Count == 0) return;

            float y = Screen.height * 0.5f + 92f;

            for (int i = 0; i < _prompts.Count; i++)
            {
                (string key, string what) = _prompts[i];

                float textWidth = UnseenUi.Body.CalcSize(new GUIContent(what)).x;
                float width = textWidth + 62f;

                var rect = new Rect(Screen.width * 0.5f - width * 0.5f, y + i * 30f, width, 26f);

                UnseenUi.Rounded(rect, UnseenUi.Ink, UnseenUi.SmallRadius, UnseenUi.Edge, 1f);

                // The key drawn as a cap rather than run into the sentence. It is the part a player
                // is looking for, and "E  loot the chest" makes them read the whole line to find it.
                var cap = new Rect(rect.x + 5f, rect.y + 5f, 22f, 16f);
                UnseenUi.Rounded(cap, new Color(1f, 1f, 1f, 0.16f), 3f);

                var centred = new GUIStyle(UnseenUi.Section)
                {
                    alignment = TextAnchor.MiddleCenter
                };

                UnseenUi.Say(cap, key, centred, UnseenUi.Text);
                UnseenUi.Say(new Rect(rect.x + 34f, rect.y, rect.width - 40f, rect.height), what,
                    UnseenUi.Body, UnseenUi.Muted);
            }
        }

        /// <summary>Reused between frames so a per-frame prompt list allocates nothing.</summary>
        private readonly List<(string Key, string What)> _prompts = new List<(string, string)>(2);

        /// <summary>The three utility slots, so 1/2/3 say what they will do before you press them.</summary>
        private void DrawUtilityBar()
        {
            if (_snapshot == null) return;

            const float slotWidth = 104f, slotHeight = 34f, spacing = 8f;
            float total = slotWidth * 3f + spacing * 2f;
            float x = Screen.width * 0.5f - total * 0.5f;
            float y = Screen.height - 52f;

            for (int i = 0; i < 3; i++)
            {
                var rect = new Rect(x + i * (slotWidth + spacing), y, slotWidth, slotHeight);
                byte effect = _snapshot.SelfUtility[i];
                bool filled = effect != 0;

                // An empty slot is drawn as an outline with nothing in it, rather than as a filled
                // box with a dash. The shape alone then says how many you are carrying.
                if (filled)
                    UnseenUi.Rounded(rect, UnseenUi.Ink, UnseenUi.SmallRadius, UnseenUi.Edge, 1f);
                else
                    UnseenUi.Rounded(rect, new Color(0f, 0f, 0f, 0.25f), UnseenUi.SmallRadius,
                        new Color(1f, 1f, 1f, 0.06f), 1f);

                var cap = new Rect(rect.x + 7f, rect.y + 9f, 18f, 16f);
                UnseenUi.Rounded(cap, new Color(1f, 1f, 1f, filled ? 0.16f : 0.06f), 3f);

                var centred = new GUIStyle(UnseenUi.Section)
                {
                    alignment = TextAnchor.MiddleCenter
                };

                UnseenUi.Say(cap, (i + 1).ToString(), centred,
                    filled ? UnseenUi.Text : UnseenUi.Faint);

                UnseenUi.Say(new Rect(rect.x + 32f, rect.y, rect.width - 38f, rect.height),
                    filled ? UtilityName((UtilityEffect)effect) : "empty",
                    UnseenUi.Label, filled ? UnseenUi.Text : UnseenUi.Faint);
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

                // Rounded, so a smeared marker reads as a soft band rather than a bar of pixels.
                UnseenUi.Rounded(rect, new Color(1f, 0.94f, 0.78f, alpha * 0.85f), 3f);

                if (ping.Occlusion < 0.3f && ping.Intensity > 0.5f)
                    UnseenUi.Say(new Rect(rect.x - 12f, rect.y - 19f, 120f, 18f),
                        ping.Kind.ToString().ToLowerInvariant(), UnseenUi.Caption,
                        UnseenUi.Text, alpha);
            }
        }

        private float _frameMs;

        private void DrawDebug()
        {
            var rect = new Rect(22f, 18f, 430f, 176f);
            UnseenUi.Panel(rect);

            UnseenUi.Say(new Rect(rect.x + 16f, rect.y + 8f, rect.width - 32f, 18f),
                "DIAGNOSTICS  ·  F3", UnseenUi.Section, UnseenUi.Faint);

            _frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.06f);

            string body =
                $"frame {_frameMs:0.0} ms ({(_frameMs > 0.01f ? 1000f / _frameMs : 0f):0} fps)\n" +
                $"snapshots {View?.SnapshotsReceived ?? 0}   proxies {View?.ProxyCount ?? 0}\n" +
                $"bytes in {(View?.BytesReceived ?? 0) / 1024} KiB\n" +
                $"tick {_snapshot?.Tick ?? 0}   visible {_snapshot?.Entities.Count ?? 0}\n" +
                $"self {_snapshot?.SelfId.ToString() ?? "-"}   " +
                $"loco {(LocomotionState)(_snapshot?.SelfLocomotion ?? 0)}\n" +
                $"flags {(AgentFlags)(_snapshot?.SelfFlags ?? 0)}";

            UnseenUi.Say(new Rect(rect.x + 16f, rect.y + 26f, rect.width - 32f, rect.height - 34f),
                body, UnseenUi.Caption, UnseenUi.Muted);
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
