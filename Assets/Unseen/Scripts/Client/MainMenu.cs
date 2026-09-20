using System;
using UnityEngine;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// What the player sees before a match: who they are, and which game they want.
    ///
    /// The game used to drop straight into a match on launch, which is fine for the person building
    /// it and baffling for everybody else - there was no way to choose a name, no way to reach
    /// another machine without editing a shortcut, and no way out except Alt-F4.
    ///
    /// Everything it collects is a launch option that already existed. The menu is a front end for
    /// the command line rather than a second way of starting the game, which is why a dedicated
    /// server never sees it: a machine started with -server has already been told what to do, and
    /// waiting for a click it will never get is how a fleet fails to come up.
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        private enum Page
        {
            Root,
            Solo,
            Host,
            Join
        }

        /// <summary>Called with the chosen launch when the player commits to something.</summary>
        private Action _begin;

        private UnseenBootstrap _boot;
        private GameSettings _settings;
        private SettingsMenu _settingsMenu;
        private LanDiscovery _discovery;

        private Page _page = Page.Root;
        private string _name = "";
        private string _address = "127.0.0.1:7777";
        private string _serverName = "";
        private int _bots = 63;
        private Vector2 _scroll;
        private string _trouble;

        /// <summary>
        /// Whether a menu should be shown at all.
        ///
        /// Never for a dedicated server, and never when the command line has already said what to
        /// do. Somebody who typed `-connect 10.0.0.7:7777` has chosen; putting a menu in front of
        /// them would be ignoring what they asked for.
        ///
        /// Whether there is anybody at the keyboard is the caller's business rather than this
        /// function's. Reading Application.isBatchMode here would make the policy depend on the
        /// environment it happens to be evaluated in - which, among other things, is why the first
        /// version of this could not be tested at all.
        /// </summary>
        public static bool ShouldShow(LaunchMode mode, bool modeWasChosen)
        {
            if (mode == LaunchMode.DedicatedServer) return false;

            return !modeWasChosen;
        }

        public void Open(UnseenBootstrap boot, Action begin)
        {
            _boot = boot;
            _begin = begin;

            _settings = GameSettings.Load();
            _name = PlayerName.Sanitise(_settings != null ? _settings.PlayerName : null);

            // Listening from the moment the menu opens, so the list is populated by the time
            // somebody reaches it rather than filling in under their cursor.
            _discovery = new LanDiscovery(listening: true);
            if (!_discovery.IsUsable) _trouble = "cannot search this network: " + _discovery.Problem;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Update()
        {
            _discovery?.Poll(Time.unscaledDeltaTime, null, 0, 0, 0);
        }

        private void OnDestroy()
        {
            _discovery?.Dispose();
        }

        private void OnGUI()
        {
            if (_begin == null) return;

            var screen = new Rect(0f, 0f, Screen.width, Screen.height);
            UnseenUi.Fill(screen, UnseenUi.Ink);

            float width = Mathf.Min(520f, Screen.width - 48f);
            float height = Mathf.Min(560f, Screen.height - 48f);
            var panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f,
                width, height);

            UnseenUi.Panel(panel);
            UnseenUi.Accented(panel, UnseenUi.Accent);

            var title = new Rect(panel.x + 28f, panel.y + 24f, panel.width - 56f, 44f);
            UnseenUi.Say(title, "UNSEEN", UnseenUi.Display, UnseenUi.Text);

            var body = new Rect(panel.x + 28f, panel.y + 86f, panel.width - 56f, panel.height - 150f);

            switch (_page)
            {
                case Page.Solo: Solo(body); break;
                case Page.Host: Host(body); break;
                case Page.Join: Join(body); break;
                default: Root(body); break;
            }

            // Which build this is, in the corner. The cheapest possible answer to "what were you
            // running when it did that", and the only place a player will ever see it.
            var stamp = new Rect(panel.x + 28f, panel.yMax - 34f, panel.width - 56f, 20f);
            UnseenUi.Say(stamp, BuildStamp.Current.Describe(), UnseenUi.Caption, UnseenUi.Faint);
        }

        private void Root(Rect area)
        {
            float y = area.y;

            y = NameField(area, y);
            y += 14f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Single player", primary: true))
                _page = Page.Solo;
            y += 48f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Host a game"))
            {
                _serverName = string.IsNullOrEmpty(_serverName) ? _name + "'s game" : _serverName;
                _page = Page.Host;
            }
            y += 48f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Join a game"))
                _page = Page.Join;
            y += 48f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Settings"))
                OpenSettings();
            y += 48f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Exit"))
                Leave();
        }

        private float NameField(Rect area, float y)
        {
            UnseenUi.Say(new Rect(area.x, y, area.width, 20f), "Name", UnseenUi.Caption,
                UnseenUi.Muted);
            y += 22f;

            GUI.SetNextControlName("name");
            _name = GUI.TextField(new Rect(area.x, y, area.width, 30f), _name,
                PlayerName.MaxLength);
            y += 34f;

            // Shown as the server would grant it, not as typed.
            //
            // The server sanitises and may suffix a name that collides, so a player who was not
            // shown the result would learn what they are actually called by reading it over
            // somebody else's head in a match.
            string granted = PlayerName.Sanitise(_name);

            if (granted != _name)
            {
                UnseenUi.Say(new Rect(area.x, y, area.width, 18f), $"you will appear as \"{granted}\"",
                    UnseenUi.Caption, UnseenUi.Gold);
            }

            return y + 20f;
        }

        private void Solo(Rect area)
        {
            float y = area.y;

            UnseenUi.Heading(new Rect(area.x, y, area.width, 24f), "Single player");
            y += 40f;

            UnseenUi.Say(new Rect(area.x, y, area.width, 20f), $"Bots: {_bots}", UnseenUi.Body,
                UnseenUi.Text);
            y += 26f;

            _bots = Mathf.RoundToInt(
                UnseenUi.Slider(new Rect(area.x, y, area.width, 24f), _bots, 1f, 63f));
            y += 40f;

            UnseenUi.Say(new Rect(area.x, y, area.width, 40f),
                "A town to yourself, and however many ninja you can stand.",
                UnseenUi.Caption, UnseenUi.Muted);
            y += 56f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Begin", primary: true))
                Start(LaunchMode.OfflinePractice, _bots + 1, null, null);

            Back(area);
        }

        private void Host(Rect area)
        {
            float y = area.y;

            UnseenUi.Heading(new Rect(area.x, y, area.width, 24f), "Host a game");
            y += 40f;

            UnseenUi.Say(new Rect(area.x, y, area.width, 20f), "Server name", UnseenUi.Caption,
                UnseenUi.Muted);
            y += 22f;

            _serverName = GUI.TextField(new Rect(area.x, y, area.width, 30f), _serverName,
                LanBeacon.MaxNameLength);
            y += 40f;

            UnseenUi.Say(new Rect(area.x, y, area.width, 40f),
                $"Others on this network will see it in their list. Port {UnseenTransport.DefaultPort}.",
                UnseenUi.Caption, UnseenUi.Muted);
            y += 56f;

            if (UnseenUi.Button(new Rect(area.x, y, area.width, 40f), "Start", primary: true))
                Start(LaunchMode.ListenServer, _bots + 1, null, _serverName);

            Back(area);
        }

        private void Join(Rect area)
        {
            float y = area.y;

            UnseenUi.Heading(new Rect(area.x, y, area.width, 24f), "Join a game");
            y += 36f;

            if (!string.IsNullOrEmpty(_trouble))
            {
                // Said plainly rather than left as an empty list. "No games found" on a network
                // that refuses broadcast is a lie, and the player's next move - typing an address -
                // is one they will only think of if they are told searching did not work.
                UnseenUi.Say(new Rect(area.x, y, area.width, 34f), _trouble, UnseenUi.Caption,
                    UnseenUi.Blood);
                y += 38f;
            }

            var list = new Rect(area.x, y, area.width, 150f);
            UnseenUi.Panel(list, 0.5f);

            int found = 0;
            _scroll = GUI.BeginScrollView(list, _scroll,
                new Rect(0f, 0f, list.width - 18f, Mathf.Max(list.height, _discovery.Count * 34f)));

            foreach (FoundServer server in _discovery.Servers)
            {
                var row = new Rect(4f, found * 34f + 4f, list.width - 26f, 28f);

                if (UnseenUi.Button(row, server.Describe()))
                {
                    _address = server.Address;
                    Start(LaunchMode.Client, 0, server.Address, null);
                }

                found++;
            }

            GUI.EndScrollView();

            if (found == 0 && string.IsNullOrEmpty(_trouble))
            {
                UnseenUi.Say(new Rect(list.x + 12f, list.y + 12f, list.width - 24f, 24f),
                    "searching for games on this network...", UnseenUi.Caption, UnseenUi.Faint);
            }

            y += 162f;

            UnseenUi.Say(new Rect(area.x, y, area.width, 20f), "or an address", UnseenUi.Caption,
                UnseenUi.Muted);
            y += 22f;

            _address = GUI.TextField(new Rect(area.x, y, area.width - 96f, 30f), _address, 64);

            if (UnseenUi.Button(new Rect(area.xMax - 88f, y, 88f, 30f), "Join", primary: true))
                Start(LaunchMode.Client, 0, _address, null);

            Back(area);
        }

        private void Back(Rect area)
        {
            if (UnseenUi.Button(new Rect(area.x, area.yMax - 36f, 110f, 32f), "Back"))
                _page = Page.Root;
        }

        private void OpenSettings()
        {
            if (_settingsMenu == null) _settingsMenu = gameObject.AddComponent<SettingsMenu>();
            _settingsMenu.Toggle();
        }

        /// <summary>
        /// Commits to a game: remembers the name, sets the launch options, and boots.
        ///
        /// Everything set here is an option the command line already had. The menu is a front end
        /// for that rather than a second way of starting the game, so there is one path into a
        /// match and one place where it can be wrong.
        /// </summary>
        private void Start(LaunchMode mode, int entities, string address, string serverName)
        {
            string granted = PlayerName.Sanitise(_name);

            if (_settings != null)
            {
                _settings.PlayerName = granted;
                _settings.Save();
            }

            UnseenTransport.RequestedName = granted;

            if (mode == LaunchMode.Client)
            {
                if (!ConnectTarget.TryParse(address, out NetEndpoint target))
                {
                    // Refused rather than silently starting a single-player game, which is what
                    // falling through would do: the player would be looking at a town wondering
                    // where everybody was.
                    _trouble = $"could not read \"{address}\" - expected host:port";
                    return;
                }

                UnseenTransport.ConnectTo = target;
            }
            else
            {
                UnseenTransport.ConnectTo = null;
                UnseenTransport.ListenPort = UnseenTransport.DefaultPort;
            }

            if (_boot != null)
            {
                _boot.Mode = mode;
                if (entities > 0) _boot.Config.Match.TargetEntityCount = Mathf.Clamp(entities, 2, 64);
                _boot.AdvertiseAs = serverName;
            }

            Action begin = _begin;
            _begin = null;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            _discovery?.Dispose();
            _discovery = null;

            begin();
            Destroy(this);
        }

        private static void Leave()
        {
            Application.Quit();

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
    }
}
