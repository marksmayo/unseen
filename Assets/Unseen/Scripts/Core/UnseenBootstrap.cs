using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.AI;
using Unseen.Audio;
using Unseen.BattleRoyale;
using Unseen.Client;
using Unseen.Combat;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Items;
using Unseen.Movement;
using Unseen.Net;
using Unseen.Perception;

namespace Unseen.Core
{
    public enum LaunchMode
    {
        /// <summary>One process, one human, 63 bots. Exercises the full replication path locally.</summary>
        OfflinePractice = 0,

        /// <summary>Headless authoritative server. No local player, no rendering.</summary>
        DedicatedServer = 1,

        /// <summary>Authoritative and playing. Useful for playtests without deploying a server.</summary>
        ListenServer = 2,

        /// <summary>Pure client. Requires a transport adapter that can actually connect.</summary>
        Client = 3
    }

    /// <summary>
    /// Single entry point. Builds the world, the transport and the simulation, then steps the
    /// authoritative loop once per frame. Everything else in the project is reachable from here,
    /// and nothing else creates systems.
    /// </summary>
    public sealed class UnseenBootstrap : MonoBehaviour
    {
        [Header("Launch")]
        public LaunchMode Mode = LaunchMode.OfflinePractice;

        [Tooltip("Overridden by -seed on the command line.")]
        public int Seed = 20260824;

        [Tooltip("Config asset. Falls back to Resources/UnseenConfig, then to code defaults.")]
        public UnseenConfig Config;

        [Header("Content")]
        [Tooltip("Optional agent prefab. A capsule ninja is assembled in code when empty.")]
        public GameObject AgentPrefab;

        [Tooltip("Optional client-side proxy prefab.")]
        public GameObject ProxyPrefab;

        [Tooltip("Optional smoke volume prefab.")]
        public GameObject SmokePrefab;

        [Tooltip("Generates the greybox castle town at runtime when the scene has no MapDescriptor.")]
        public bool GenerateGreyboxIfEmpty = true;

        [Tooltip("Builds a NavMesh over the town at startup. Off, bots fall back to whisker " +
                 "steering, which is what they did for the whole project before this existed.")]
        public bool BuildNavMesh = true;

        [Header("Diagnostics")]
        [Tooltip("Logs a server status line at this interval. Zero disables it.")]
        public float StatusLogInterval = 5f;

        public bool VerboseStartup = true;

        private ServerSimulation _sim;
        private SimContext _ctx;
        private INetworkService _net;
        private AgentSpawner _spawner;
        private MatchDirector _match;
        private SimProfile _profile;
        private BotDirector _bots;
        private PlayerSeatSystem _seats;

        /// <summary>Lobby wait from the command line, applied once the match director exists.</summary>
        private float? _lobbySeconds;
        private ReplicationSystem _replication;
        private CombatPocketSystem _pockets;
        private MotionSystem _motion;
        private InterestManager _interest;
        private ClientNetworkView _clientView;
        private LocalAgentDriver _localAgent;
        private PlayerInputSource _input;
        private ThirdPersonCameraRig _camera;
        private float _nextStatusLogAt;

        /// <summary>Health and drain state, shared with the systems that have to respect it.</summary>
        private readonly ServerLifecycle _life = new ServerLifecycle();

        private bool _quitting;

        public SimContext Context => _ctx;
        public ServerSimulation Simulation => _sim;
        public INetworkService Network => _net;

        /// <summary>The map this boot resolved. Exposed so tools can check bounds against it.</summary>
        public MapDescriptor Map { get; private set; }

        /// <summary>The bot director. Exposed so probes can exercise spawn placement directly.</summary>
        public AI.BotDirector Bots => _bots;
        public MatchDirector Match => _match;
        public ClientNetworkView ClientView => _clientView;

        private bool _booted;

        private void Awake()
        {
            if (FirstLoadIntro.ShouldPlay(Mode == LaunchMode.DedicatedServer))
            {
                var intro = new GameObject("First Load Intro");
                intro.transform.SetParent(transform, false);
                intro.AddComponent<FirstLoadIntro>().Play(Boot);
            }
            else Boot();
        }

        /// <summary>
        /// Builds the world, the transport and the simulation after the first-load intro;
        /// editor tooling calls it directly because Awake does not run in edit mode.
        /// </summary>
        public void Boot()
        {
            if (_booted) return;
            _booted = true;

            // Config first: the command line can override values inside it.
            Config = Config != null ? Config : UnseenConfig.Default;
            ApplyCommandLine();
            UnseenLayers.ApplyCollisionMatrix();
            Application.targetFrameRate = Mode == LaunchMode.DedicatedServer ? Config.Network.CombatTickRate : -1;
            QualitySettings.vSyncCount = Mode == LaunchMode.DedicatedServer ? 0 : 1;

            // A dedicated server still simulates smoke - agents inside a cloud really are harder
            // to see, and that has to be decided server side - but it has nobody to draw it for.
            Environment.SmokeCloud.BuildVisuals = Mode != LaunchMode.DedicatedServer;

            _net = CreateNetworkService();
            _ctx = new SimContext(Config, transform, _net, Seed);

            // Registered so the seat system can refuse players while draining. A server on its way
            // out is not a place to send somebody who will load for a minute and be dropped.
            _ctx.Register(_life);

            Application.wantsToQuit += OnWantsToQuit;
            _ctx.Sound = new SoundEventBus();
            _ctx.Destructibles = new DestructibleRegistry();

            MapDescriptor map = ResolveMap();

            // The NavMesh, now that there is a town to build it over.
            //
            // Must be here rather than in the editor: the town is generated at startup and differs
            // by seed, so there is no level to bake in advance. BotNavigator has been written to
            // use one since it was first committed and has silently fallen through to whisker
            // steering every time, because nothing was ever built.
            if (BuildNavMesh)
            {
                bool built = Unseen.AI.NavMeshBaker.Build(map);

                if (VerboseStartup || StatusLogInterval > 0f)
                    UnseenLog.Info($"[Unseen] navmesh: built={built} in " +
                              $"{Unseen.AI.NavMeshBaker.LastBakeSeconds:0.00} s, " +
                              $"{Unseen.AI.NavMeshBaker.LastCarvedVolumes} water volume(s) carved " +
                              $"out as unwalkable");
            }
            Map = map;

            _spawner = new AgentSpawner(_ctx, transform, AgentPrefab, Mode != LaunchMode.DedicatedServer);
            BuildSimulation(map);

            if (_net is OfflineNetworkService offline && Mode != LaunchMode.DedicatedServer)
            {
                // Firing the connect event drives the same backfill path a real client would take.
                offline.Start();
            }

            if (Mode != LaunchMode.DedicatedServer) BuildClientRig();

            if (VerboseStartup)
            {
                UnseenLog.Info($"[Unseen] booted as {Mode} seed {Seed} | {_ctx.Destructibles.Describe()} | " +
                          $"tick {Config.Network.BaseTickRate}/{Config.Network.CombatTickRate} Hz");
            }
        }

        /// <summary>
        /// Applies the command line.
        ///
        /// The reading is in LaunchOptions, which takes an array and can therefore be tested; this
        /// only decides what to do with the answers. Clamping entities stays here because it needs
        /// the config, and parsing has no business knowing about that.
        /// </summary>
        private void ApplyCommandLine()
        {
            LaunchOptions options = LaunchOptions.Parse(System.Environment.GetCommandLineArgs());

            // Only when the command line actually chose one. Applying the default unconditionally
            // would overwrite a mode set in the inspector or by a tool - the screenshot capture
            // asks for ListenServer before booting, and would have been demoted to offline practice
            // on every run, silently.
            if (options.HasMode) Mode = options.Mode;

            if (options.HasSeed) Seed = options.Seed;

            if (options.HasEntities && Config != null)
                Config.Match.TargetEntityCount = Mathf.Clamp(options.Entities, 1, 128);

            Net.UnseenTransport.ListenPort = options.ListenPort;
            Net.UnseenTransport.ConnectTo = options.ConnectTo;
            Net.UnseenTransport.RequestedName = options.RequestedName;
            Net.UnseenTransport.Conditions = options.Conditions;
            _lobbySeconds = options.LobbySeconds;

            if (!string.IsNullOrEmpty(options.Error)) UnseenLog.Error($"[Unseen] {options.Error}");
        }

        private INetworkService CreateNetworkService()
        {
            // Transport adapters register themselves here. The loopback service is always available
            // and is what the offline and soak-test paths use.
            INetworkService adapter = UnseenTransport.Create(Mode);
            return adapter ?? new OfflineNetworkService();
        }

        private MapDescriptor ResolveMap()
        {
            MapDescriptor map = MapDescriptor.Find();
            if (map != null) return map;

            if (!GenerateGreyboxIfEmpty)
            {
                var placeholder = new GameObject("MapDescriptor").AddComponent<MapDescriptor>();
                placeholder.Radius = 200f;
                return placeholder;
            }

            var generatorHost = new GameObject("GreyboxTown");
            GreyboxTownGenerator generator = generatorHost.AddComponent<GreyboxTownGenerator>();
            generator.Seed = Seed;
            generator.EnableVisualUpgrade = Mode != LaunchMode.DedicatedServer;
            return generator.Generate();
        }

        private void BuildSimulation(MapDescriptor map)
        {
            _profile = SimProfile.For(Mode);
            _sim = new ServerSimulation(_ctx);

            // Movement runs on both sides of the wire, and it is deliberately the same motor on
            // both. A client has to move its own ninja the instant the key goes down rather than
            // wait out the round trip, and a prediction written as a second implementation of
            // movement disagrees with the first - so every correction the server sends would snap
            // the player somewhere they did not ask to be.
            _motion = _sim.Add(new MotionSystem());
            WorldBoundsSystem bounds = _sim.Add(new WorldBoundsSystem());

            DeploymentSystem deployment = null;
            MistZoneController mist = null;
            Unseen.Perception.DrowningSystem drowning = null;
            Unseen.Combat.ShurikenSystem shuriken = null;

            if (_profile.ResolvesPerception)
            {
                _sim.Add(new WorldBufferSystem());
                _sim.Add(new InterestGridSystem());
                _sim.Add(new StealthIndexService());
                _interest = _sim.Add(new InterestManager());
                _sim.Add(new AcousticPropagation());
                _pockets = _sim.Add(new CombatPocketSystem());
            }

            if (_profile.OwnsTheMatch)
            {
                _sim.Add(new ServerInputSystem());
                _bots = _sim.Add(new BotDirector());
                _seats = _sim.Add(new PlayerSeatSystem());
                deployment = _sim.Add(new DeploymentSystem());
                CombatDirector combat = _sim.Add(new CombatDirector());
                combat.SmokePrefab = SmokePrefab;
                _sim.Add(new AgentEffectsSystem());
                _match = _sim.Add(new MatchDirector());
                if (_lobbySeconds.HasValue) _match.LobbyTimeout = _lobbySeconds.Value;
                mist = _sim.Add(new MistZoneController());
                _bamboo = _sim.Add(new BambooGrowthSystem());
                _sim.Add(new Unseen.Perception.CritterStartleSystem());
                drowning = _sim.Add(new Unseen.Perception.DrowningSystem());
                shuriken = _sim.Add(new Unseen.Combat.ShurikenSystem());
                _sim.Add(new Unseen.Perception.FootprintSystem());
            }

            if (_profile.Replicates) _replication = _sim.Add(new ReplicationSystem());

            _sim.Initialize();

            float3 center = map != null ? (float3)map.Center : float3.zero;
            float radius = map != null ? map.Radius : 200f;

            bounds.Configure(map);

            // Destructible ids are agreed by both sides from sorted positions rather than by a
            // handshake, so a client builds the same index a server does - it has to, or a shoji
            // the server says is broken is a different shoji here.
            _ctx.Destructibles.BuildIndex();

            // Everything past here belongs to whoever is running the match. A client is told all
            // of it.
            if (!_profile.OwnsTheMatch) return;

            // The spirit forest belongs to the level, so the system is handed the one the
            // generator planted rather than building its own.
            var forest = map != null ? map.GetComponentInChildren<BambooForest>() : null;
            if (forest == null) forest = FindAnyObjectByType<BambooForest>();
            if (forest != null) _bamboo.Configure(forest, center, radius, mist);
            else Debug.LogWarning("[Unseen] no BambooForest in the level; the spirit forest will not grow");

            _match.MatchStarted += _ => _bamboo.Begin(_sim.Time);

            // Birds back on their branches for a new match, or the second match of a session is
            // played in a town where everything has already been frightened off.
            _match.MatchStarted += _ => Unseen.Environment.Critter.ResetAll();

            // Fresh lungs for a new match, or somebody who drowned in the last one starts this one
            // already out of air.
            _match.MatchStarted += _ => drowning.Reset();

            // Blades cleared and everyone re-armed for a new match, or the town starts covered in
            // the last one's litter.
            _match.MatchStarted += _ => shuriken.Reset();

            // Put the forest away when the match ends, or the ring stays standing through the
            // results screen and the next lobby - fourteen metres of bamboo around an empty town.
            _match.MatchEnded += _ => _bamboo.Stop();
            _match.AgentDied += OnAgentDied;
            _match.MatchStarted += _ => _hud?.NoteMatchStarted();
            _match.Configure(_spawner, center, radius, Seed);
            _bots.Configure(_spawner, center, radius);
            _seats.Configure(_spawner, _bots);

            // Deployment registers itself for lookup by the match director.
            _ctx.Register(deployment);
        }

        private void BuildClientRig()
        {
            var rig = new GameObject("LocalPlayer");
            rig.transform.SetParent(transform, false);

            _input = rig.AddComponent<PlayerInputSource>();

            _clientView = rig.AddComponent<ClientNetworkView>();
            _clientView.ProxyPrefab = ProxyPrefab;
            _clientView.SmokePrefab = SmokePrefab;
            _clientView.Bind(_net, Config, _ctx.Destructibles, _input, _ctx.Entities);

            // A pure client owns exactly one agent - its own - and is told about every other thing
            // in the world. On a host or offline build the local agent is the server's own agent,
            // already simulated in this process, so there is no round trip to hide and nothing for
            // prediction to do but correct itself against itself.
            if (!_profile.OwnsTheMatch)
            {
                _localAgent = rig.AddComponent<LocalAgentDriver>();
                _localAgent.View = _clientView;
                _localAgent.Bind(_ctx, _net, _spawner, _input, Net.UnseenTransport.RequestedName);
            }

            var cameraHost = new GameObject("PlayerCamera");
            cameraHost.tag = "MainCamera";
            cameraHost.transform.SetParent(rig.transform, false);
            _camera = cameraHost.AddComponent<ThirdPersonCameraRig>();
            _camera.Input = _input;

            var feedback = rig.AddComponent<CombatFeedback>();
            feedback.View = _clientView;
            feedback.CameraRig = _camera;

            EnablePostProcessing(cameraHost);

            StealthHud hud = rig.AddComponent<StealthHud>();
            hud.View = _clientView;
            hud.Input = _input;
            _hud = hud;

            MinimapHud minimap = rig.AddComponent<MinimapHud>();
            minimap.View = _clientView;
            minimap.Input = _input;

            SettingsMenu menu = rig.AddComponent<SettingsMenu>();
            menu.Input = _input;

            var sound = rig.AddComponent<Unseen.Audio.SoundRenderer>();
            sound.View = _clientView;

            _localSound = rig.AddComponent<Unseen.Audio.LocalSoundEmitter>();

            cameraHost.AddComponent<Unseen.Audio.AmbientWind>();

            ShojiSilhouetteFeeder feeder = rig.AddComponent<ShojiSilhouetteFeeder>();
            feeder.View = _clientView;

            MistVisual mist = rig.AddComponent<MistVisual>();
            mist.View = _clientView;

            cameraHost.AddComponent<AudioListener>();
        }

        /// <summary>
        /// Turns on tone mapping and a modest exposure lift.
        ///
        /// Without it a moonlit scene renders correct but unreadable: linear output crushes
        /// everything below the lantern pools to black. The point is legibility, not brightness -
        /// shadows must still read as darker than lit ground, because that difference is the whole
        /// stealth read for the player.
        /// </summary>
        private void EnablePostProcessing(GameObject cameraHost)
        {
            var cameraData = cameraHost.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (cameraData == null)
                cameraData = cameraHost.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();

            cameraData.renderPostProcessing = true;
            cameraData.renderShadows = true;

            // SMAA on top of the pipeline's MSAA, because this town is built out of exactly the
            // geometry MSAA handles worst: bamboo culms, roof edges and balcony rails, all thin and
            // all high contrast against a dark sky. SMAA rather than TAA - TAA needs motion vectors
            // and smears a moving silhouette, and a smeared silhouette is a stealth game telling
            // the player a lie about where someone is standing.
            cameraData.antialiasing =
                UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cameraData.antialiasingQuality = UnityEngine.Rendering.Universal.AntialiasingQuality.High;

            var volumeHost = new GameObject("PostProcessing");
            volumeHost.transform.SetParent(cameraHost.transform, false);

            var volume = volumeHost.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;

            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();

            // ACES rather than Neutral.
            //
            // Neutral is a straight-through curve and it is the right default for a game that wants
            // to show you what it drew. This one wants a deep toe and a rolled shoulder: shadows
            // that crush toward black and lantern flames that bloom out rather than clipping flat.
            // That is most of the difference between "a dark scene" and "a night scene".
            var tonemapping = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>(true);
            tonemapping.mode.Override(UnityEngine.Rendering.Universal.TonemappingMode.ACES);

            var colour = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            colour.postExposure.Override(1.05f);
            colour.contrast.Override(2.5f);
            colour.saturation.Override(-4f);

            // Cold shadows, warm lights. This is the whole palette in one effect: everything unlit
            // falls toward indigo and everything a lantern touches goes amber, which is what makes
            // a single paper lamp read as fire from across a courtyard.
            var split = profile.Add<UnityEngine.Rendering.Universal.SplitToning>(true);
            split.shadows.Override(new Color(0.39f, 0.44f, 0.55f));
            split.highlights.Override(new Color(0.92f, 0.66f, 0.34f));
            split.balance.Override(-8f);

            // Bloom, thresholded above everything except flame.
            //
            // Nothing in this town is bright except the lanterns, so a threshold just under their
            // output makes them the only thing that blooms - and a lantern with a halo is the
            // difference between a lit texture and a light source.
            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            // Above anything a moonlit surface can reach. At 0.85 the lit side of a hedge was
            // blooming as hard as the lamp lighting it, which is how a row of shrubs came out
            // acid green.
            bloom.threshold.Override(1.15f);
            bloom.intensity.Override(.32f);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.86f, 0.66f));

            // A vignette, because both reference images have a heavy one and because it does the
            // same job as a shadowed proscenium: it pushes the eye to the middle of the frame and
            // makes the edges feel like somewhere you cannot see into.
            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.Override(0.09f);
            vignette.smoothness.Override(0.42f);
            vignette.color.Override(new Color(0.02f, 0.03f, 0.06f));

            // Just enough grain to stop a very dark, very smooth night from banding, which it will
            // at these exposures on an eight bit display.
            var grain = profile.Add<UnityEngine.Rendering.Universal.FilmGrain>(true);
            grain.type.Override(UnityEngine.Rendering.Universal.FilmGrainLookup.Thin1);
            grain.intensity.Override(0.10f);
            grain.response.Override(0.85f);

            volume.profile = profile;

            // Brightness is exposed as a setting because "how dark is too dark" is a monitor
            // question, not a design one, and this is a game that asks the player to read shadow.
            _exposure = colour;
            ApplyBrightness(GameSettings.Current);
            GameSettings.Changed += ApplyBrightness;

            UnseenLog.Info($"[Unseen] post-processing: volume created, postExposure 1.9, " +
                      $"renderPostProcessing={cameraData.renderPostProcessing}, " +
                      $"volumeMask={cameraData.volumeLayerMask.value}, " +
                      $"pipeline={UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.name ?? "none"}");
        }

        private UnityEngine.Rendering.Universal.ColorAdjustments _exposure;
        private Unseen.Audio.LocalSoundEmitter _localSound;
        private StealthHud _hud;
        private AgentEntity _localSoundAgent;
        private AgentEntity _spectating;
        private BambooGrowthSystem _bamboo;

        /// <summary>
        /// Plays the death scene, and reports the elimination to the HUD.
        ///
        /// Hooked to the server event rather than to the replicated combat event, because in every
        /// mode that exists today the agent GameObjects are the things being rendered. The client
        /// path in ClientNetworkView covers remote proxies for when a real transport lands.
        /// </summary>
        private void OnAgentDied(AgentEntity victim, AgentEntity killer)
        {
            var death = victim.GetComponent<AgentDeathVisual>();
            if (death != null)
            {
                float3 from = killer != null && killer != victim
                    ? victim.Position - killer.Position
                    : float3.zero;
                death.Play(-(Vector3)from);
            }

            if (_hud != null) _hud.NoteElimination(victim, killer);
        }

        private void ApplyBrightness(GameSettings settings)
        {
            if (_exposure == null || settings == null) return;
            _exposure.postExposure.Override(.85f * settings.Brightness);
        }

        private void Update()
        {
            if (!_booted || _sim == null) return;
            float dt = Time.deltaTime;

            _net.Poll(dt);
            _sim.Advance(dt);

            // Health is "the simulation stepped", not "the process is running". A server that is
            // up but not simulating is the worst case for everybody in it: the port still accepts,
            // the pod still looks alive, and sixty-four players stand frozen in a town.
            _life.Ticked(_sim.Time);
            LeaveIfDrained();

            BindCameraToLocalAgent();
            LogStatus();
        }

        /// <summary>
        /// Quits once a drain has finished, and not before.
        ///
        /// The deadline is the lifecycle's, not Kubernetes'. A match that will not end would
        /// otherwise hold the pod until the grace period expires and SIGKILL arrives - which is
        /// precisely the abrupt ending draining exists to avoid, so it leaves on its own terms
        /// while it still can.
        /// </summary>
        private void LeaveIfDrained()
        {
            if (!_life.IsDraining) return;

            bool matchRunning = _match != null &&
                                _match.Phase != BattleRoyale.MatchPhase.Lobby &&
                                _match.Phase != BattleRoyale.MatchPhase.PostMatch;

            if (!_life.ShouldExit(_sim.Time, matchRunning)) return;

            UnseenLog.Info($"[Unseen] drained after {_life.Describe(_sim.Time)}; exiting");
            _quitting = true;
            Application.Quit();
        }

        /// <summary>
        /// Takes a shutdown request as "finish what you are doing", not "stop now".
        ///
        /// This is the whole of "a server update must not kill matches in progress". A rolling
        /// deploy asks every server in the fleet to go, one at a time, and a server that obeys
        /// immediately ends a match for sixty-four people - the update arriving as a crash, from
        /// the player's side. Refusing the quit once, draining, and leaving when the match is over
        /// turns a deploy into something nobody in the game notices.
        /// </summary>
        private bool OnWantsToQuit()
        {
            if (_quitting || _life == null) return true;

            _life.Drain(_sim != null ? _sim.Time : 0f);
            UnseenLog.Info("[Unseen] asked to stop; draining rather than dropping the match");

            return false;
        }

        /// <summary>
        /// On a host or offline build the local agent lives in this process, so the camera follows
        /// the authoritative transform directly. A pure client would instead follow its own proxy.
        /// </summary>
        private void BindCameraToLocalAgent()
        {
            if (_camera == null || _net.LocalConnectionId < 0) return;

            AgentEntity local = _ctx.Entities.ByConnection(_net.LocalConnectionId);

            // No body at all: joined or rejoined while a round was under way, so there is nothing
            // to follow and nothing coming until the next match. Watching somebody else is the only
            // thing left, and it is the same view a dead player gets - which is the point, because
            // arriving mid-round and being eliminated are the same situation from the seat.
            if (local == null)
            {
                Spectate(null);
                return;
            }

            // Dead is not the same as finished.
            //
            // A killed player used to be left with the camera locked to their own corpse, which
            // then sank into the ground and switched itself off: input still registered, the match
            // ran on for minutes with sixty bots hunting each other, and nothing happened. It
            // reads as a hang, and it was reported as a crash. Spectating gives the rest of the
            // match somewhere to be watched from.
            if (!local.IsAlive)
            {
                Spectate(local);
                return;
            }

            _spectating = null;
            if (_camera.Follow != local.transform) _camera.SetTarget(local.transform);
            _camera.Crouched = local.Stance != Stance.Stand;
            _camera.Prone = local.Stance == Stance.Prone;

            // Own-audio binds here for the same reason the camera does: the agent does not exist
            // until the match spawns it, which is after the client rig is built.
            if (_localSound != null && _localSoundAgent != local)
            {
                _localSound.Bind(local, Config);
                _localSoundAgent = local;
            }

            // Keep the look angles the server is using in step with the local camera.
            if (_input != null && local.IsAlive && Mathf.Abs(UnseenMath.YawDelta(local.Yaw, _input.Yaw)) > 90f)
                _input.SetLook(local.Yaw, local.Pitch);
        }

        /// <summary>
        /// Follows a living agent while the local player is dead, cycling on the jump key.
        ///
        /// Deliberately not a free camera: this is a stealth game whose whole information model is
        /// about what a body can see from where it stands, and a detached flying camera would show
        /// a dead player things a live one could never earn.
        /// </summary>
        private void Spectate(AgentEntity local)
        {
            _camera.Crouched = false;

            bool cycle = _input != null && UnityEngine.Input.GetKeyDown(_input.JumpKey);
            if (_spectating != null && _spectating.IsAlive && !cycle)
            {
                if (_camera.Follow != _spectating.transform) _camera.SetTarget(_spectating.transform);
                if (_hud != null) _hud.Spectating = _spectating.DisplayName;
                return;
            }

            // Next living agent after the current one, wrapping. Skipping the local corpse is
            // implicit: it is not alive.
            IReadOnlyList<AgentEntity> all = _ctx.Entities.All;
            int start = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i] == _spectating)
                {
                    start = i + 1;
                    break;
                }

            AgentEntity next = null;
            for (int offset = 0; offset < all.Count; offset++)
            {
                AgentEntity candidate = all[(start + offset) % all.Count];
                if (candidate == null || !candidate.IsAlive || candidate == local) continue;
                next = candidate;
                break;
            }

            _spectating = next;

            if (next != null)
            {
                _camera.SetTarget(next.transform);
                if (_hud != null) _hud.Spectating = next.DisplayName;
            }
            else if (_hud != null)
            {
                _hud.Spectating = null;
            }
        }

        /// <summary>Mean round trip across every connection, in seconds. Zero with nobody on.</summary>
        private float AverageRoundTrip()
        {
            IReadOnlyList<int> connections = _net.Connections;
            if (connections.Count == 0) return 0f;

            float total = 0f;
            for (int i = 0; i < connections.Count; i++) total += _net.RoundTripTime(connections[i]);
            return total / connections.Count;
        }

        private void LogStatus()
        {
            if (StatusLogInterval <= 0f || Time.unscaledTime < _nextStatusLogAt) return;
            _nextStatusLogAt = Time.unscaledTime + StatusLogInterval;

            // A client has none of these systems, so it reports what it is actually doing: what
            // arrived, and how much of its own movement is still unconfirmed.
            if (!_profile.OwnsTheMatch)
            {
                long inBytes = _clientView != null ? _clientView.BytesReceived : 0L;

                UnseenLog.Info($"[Unseen] client | sim {_sim.LastFrameMilliseconds:0.00} ms | " +
                          $"in {(_clientView != null ? _clientView.SnapshotsReceived : 0)} snapshots " +
                          $"{inBytes / 1024f:0.0} KiB | " +
                          $"{(_localAgent != null && _localAgent.Agent != null ? "body" : "no body yet")}, " +
                          $"{(_localAgent != null ? _localAgent.UnconfirmedInputs : 0)} inputs unconfirmed");

                // No round trip on this line, deliberately. The server pings and the client echoes,
                // so the measurement belongs to the side that uses it - a client compensated for a
                // latency it reported itself would be choosing its own parry window. The client
                // genuinely does not know the number, and printing a zero implied it did.

                LogLocalPlayer();
                return;
            }

            // Connections and per-player bandwidth belong on this line because the whole point of
            // the number beside them is the per-player figure, and "out 900 kbps" means nothing
            // without knowing whether that was one client or sixty-four. The first load test
            // reported `out 0 kbps` for four minutes and the zero was the interesting part: nothing
            // had connected, and the line did not say so.
            int players = _net.Connections.Count;
            float perPlayer = players > 0 ? _replication.KilobitsPerSecond / players : 0f;

            UnseenLog.Info($"[Unseen] {_match.StatusLine()} | sim {_sim.LastFrameMilliseconds:0.00} ms | " +
                      $"hot {_pockets.HotAgents}/{_motion.HotAgentsLastTick} | {_interest.DescribeLoad()} | " +
                      $"{_bots.Describe()} | {players} players | " +
                      $"out {_replication.KilobitsPerSecond:0} kbps ({perPlayer:0.0} each) | " +
                      $"rtt {AverageRoundTrip() * 1000f:0} ms | {_life.Describe(_sim.Time)}");

            LogLocalPlayer();
        }

        /// <summary>
        /// Reports what the local player is actually doing. Diagnosing "I cannot move" from a
        /// screenshot is guesswork; this makes the input path and the motor state observable.
        /// </summary>
        private void LogLocalPlayer()
        {
            if (_net.LocalConnectionId < 0) return;

            AgentEntity local = _ctx.Entities.ByConnection(_net.LocalConnectionId);
            if (local == null)
            {
                UnseenLog.Info("[Unseen] local player: no agent bound to this connection");
                return;
            }

            MoveIntent intent = local.Intent;
            string camera = _camera != null && _camera.Follow != null ? _camera.Follow.name : "none";
            float inputMagnitude = math.length(intent.Move);

            var visual = local.GetComponentInChildren<Unseen.Entities.AgentVisual>();
            var skinned = local.GetComponentInChildren<SkinnedMeshRenderer>();
            int visualsInScene = FindObjectsByType<Unseen.Entities.AgentVisual>(FindObjectsSortMode.None).Length;
            Unseen.Entities.AgentVisualSet set = Unseen.Entities.AgentVisualSet.Load();

            UnseenLog.Info($"[Unseen] visual check: set={(set != null)} usable={(set != null && set.IsUsable)} " +
                      $"skins={(set != null && set.Skins != null ? set.Skins.Length : 0)} " +
                      $"| local visual={(visual != null)} skinned={(skinned != null)} " +
                      $"enabled={(skinned != null && skinned.enabled)} " +
                      $"mat={(skinned != null && skinned.sharedMaterial != null ? skinned.sharedMaterial.name : "none")} " +
                      $"shader={(skinned != null && skinned.sharedMaterial != null ? skinned.sharedMaterial.shader.name : "none")} " +
                      $"bounds={(skinned != null ? skinned.bounds.size.ToString("0.00") : "n/a")} " +
                      $"scale={(visual != null ? visual.transform.lossyScale.ToString("0.000") : "n/a")} " +
                      $"| AgentVisuals in scene={visualsInScene}");

            UnseenLog.Info($"[Unseen] local {local.DisplayName} pos {local.Position} " +
                      $"loco {local.Locomotion} stance {local.Stance} " +
                      $"alive {local.IsAlive} deployed {(local.Flags & AgentFlags.Deployed) != 0} " +
                      $"grounded {(local.Motor != null && local.Motor.IsGrounded)} " +
                      $"vel {(local.Motor != null ? math.length(local.Motor.Velocity) : 0f):0.00} " +
                      $"| intent seq {intent.Sequence} move {inputMagnitude:0.00} yaw {intent.Yaw:0} " +
                      $"| camera follows {camera} | inputSource {(_input != null ? _input.Current.Sequence.ToString() : "null")}");
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        /// <summary>Tears the simulation and transport down. Safe to call more than once.</summary>
        public void Shutdown()
        {
            if (!_booted) return;
            _booted = false;

            GameSettings.Changed -= ApplyBrightness;
            _exposure = null;

            _sim?.Dispose();
            _net?.Shutdown();
            _sim = null;
        }
    }
}
