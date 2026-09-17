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
        private BotDirector _bots;
        private ReplicationSystem _replication;
        private CombatPocketSystem _pockets;
        private MotionSystem _motion;
        private InterestManager _interest;
        private ClientNetworkView _clientView;
        private PlayerInputSource _input;
        private ThirdPersonCameraRig _camera;
        private float _nextStatusLogAt;

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
            _sim = new ServerSimulation(_ctx);

            _sim.Add(new ServerInputSystem());
            _sim.Add(new WorldBufferSystem());
            _sim.Add(new InterestGridSystem());
            _sim.Add(new StealthIndexService());
            _interest = _sim.Add(new InterestManager());
            _sim.Add(new AcousticPropagation());
            _pockets = _sim.Add(new CombatPocketSystem());
            _bots = _sim.Add(new BotDirector());
            DeploymentSystem deployment = _sim.Add(new DeploymentSystem());
            _motion = _sim.Add(new MotionSystem());
            WorldBoundsSystem bounds = _sim.Add(new WorldBoundsSystem());
            CombatDirector combat = _sim.Add(new CombatDirector());
            _sim.Add(new AgentEffectsSystem());
            _match = _sim.Add(new MatchDirector());
            MistZoneController mist = _sim.Add(new MistZoneController());
            _bamboo = _sim.Add(new BambooGrowthSystem());
            _sim.Add(new Unseen.Perception.CritterStartleSystem());
            Unseen.Perception.DrowningSystem drowning = _sim.Add(new Unseen.Perception.DrowningSystem());
            Unseen.Combat.ShurikenSystem shuriken = _sim.Add(new Unseen.Combat.ShurikenSystem());
            _sim.Add(new Unseen.Perception.FootprintSystem());
            _replication = _sim.Add(new ReplicationSystem());

            combat.SmokePrefab = SmokePrefab;

            _sim.Initialize();

            float3 center = map != null ? (float3)map.Center : float3.zero;
            float radius = map != null ? map.Radius : 200f;

            bounds.Configure(map);

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
            _ctx.Destructibles.BuildIndex();

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

            var cameraHost = new GameObject("PlayerCamera");
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
            colour.postExposure.Override(.85f);
            colour.contrast.Override(10f);
            colour.saturation.Override(-4f);

            // Cold shadows, warm lights. This is the whole palette in one effect: everything unlit
            // falls toward indigo and everything a lantern touches goes amber, which is what makes
            // a single paper lamp read as fire from across a courtyard.
            var split = profile.Add<UnityEngine.Rendering.Universal.SplitToning>(true);
            split.shadows.Override(new Color(0.24f, 0.34f, 0.62f));
            split.highlights.Override(new Color(0.92f, 0.66f, 0.34f));
            split.balance.Override(-18f);

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
            bloom.intensity.Override(.55f);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.86f, 0.66f));

            // A vignette, because both reference images have a heavy one and because it does the
            // same job as a shadowed proscenium: it pushes the eye to the middle of the frame and
            // makes the edges feel like somewhere you cannot see into.
            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.Override(0.2f);
            vignette.smoothness.Override(0.42f);
            vignette.color.Override(new Color(0.02f, 0.03f, 0.06f));

            // Just enough grain to stop a very dark, very smooth night from banding, which it will
            // at these exposures on an eight bit display.
            var grain = profile.Add<UnityEngine.Rendering.Universal.FilmGrain>(true);
            grain.type.Override(UnityEngine.Rendering.Universal.FilmGrainLookup.Thin1);
            grain.intensity.Override(0.22f);
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

            BindCameraToLocalAgent();
            LogStatus();
        }

        /// <summary>
        /// On a host or offline build the local agent lives in this process, so the camera follows
        /// the authoritative transform directly. A pure client would instead follow its own proxy.
        /// </summary>
        private void BindCameraToLocalAgent()
        {
            if (_camera == null || _net.LocalConnectionId < 0) return;

            AgentEntity local = _ctx.Entities.ByConnection(_net.LocalConnectionId);
            if (local == null) return;

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

        private void LogStatus()
        {
            if (StatusLogInterval <= 0f || Time.unscaledTime < _nextStatusLogAt) return;
            _nextStatusLogAt = Time.unscaledTime + StatusLogInterval;

            UnseenLog.Info($"[Unseen] {_match.StatusLine()} | sim {_sim.LastFrameMilliseconds:0.00} ms | " +
                      $"hot {_pockets.HotAgents}/{_motion.HotAgentsLastTick} | {_interest.DescribeLoad()} | " +
                      $"{_bots.Describe()} | out {_replication.KilobitsPerSecond:0} kbps");

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
