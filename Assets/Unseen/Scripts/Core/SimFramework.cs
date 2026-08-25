using System;
using System.Collections.Generic;
using UnityEngine;
using Unseen.AI;
using Unseen.Audio;
using Unseen.BattleRoyale;
using Unseen.Combat;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Net;
using Unseen.Perception;

namespace Unseen.Core
{
    /// <summary>One authoritative simulation step.</summary>
    public readonly struct SimFrame
    {
        /// <summary>Monotonic simulation tick at the combat rate (the sim runs its loop at the highest rate).</summary>
        public readonly int Tick;

        public readonly float Dt;
        public readonly float Time;

        /// <summary>True on ticks that align with the base spatial rate (20 Hz by default).</summary>
        public readonly bool IsBaseTick;

        public SimFrame(int tick, float dt, float time, bool isBaseTick)
        {
            Tick = tick;
            Dt = dt;
            Time = time;
            IsBaseTick = isBaseTick;
        }
    }

    public enum SimRate
    {
        /// <summary>Runs on base-rate ticks only (spatial roaming work).</summary>
        Base,

        /// <summary>Runs every tick at the combat rate.</summary>
        Combat
    }

    public interface ISimSystem
    {
        string Name { get; }

        /// <summary>Systems tick in ascending order within a frame.</summary>
        int Order { get; }

        SimRate Rate { get; }

        void Initialize(SimContext ctx);
        void Tick(in SimFrame frame);
        void Shutdown();
    }

    public abstract class SimSystem : ISimSystem
    {
        protected SimContext Ctx { get; private set; }

        public virtual string Name => GetType().Name;
        public abstract int Order { get; }
        public virtual SimRate Rate => SimRate.Base;

        public void Initialize(SimContext ctx)
        {
            Ctx = ctx;
            OnInitialize();
        }

        protected virtual void OnInitialize()
        {
        }

        public abstract void Tick(in SimFrame frame);

        public virtual void Shutdown()
        {
        }
    }

    /// <summary>
    /// Canonical tick ordering. Perception must resolve before AI decides, AI before motion,
    /// motion before combat resolution, and replication last so clients see a settled frame.
    /// </summary>
    public static class SimOrder
    {
        public const int Input = 100;
        public const int WorldBuffers = 150;
        public const int InterestGrid = 200;
        public const int Stealth = 250;
        public const int LineOfSight = 300;
        public const int Acoustics = 350;
        public const int CombatPockets = 400;
        public const int BotThink = 500;
        public const int Motion = 600;
        public const int Combat = 700;
        public const int Environment = 750;
        public const int Match = 800;
        public const int Mist = 820;
        public const int Backfill = 840;
        public const int Replication = 900;
    }

    /// <summary>Everything a system is allowed to reach. Concrete fields for hot services, lookup for the rest.</summary>
    public sealed class SimContext
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        public UnseenConfig Config { get; }
        public Transform Root { get; }
        public INetworkService Net { get; }
        public EntityRegistry Entities { get; }
        public WorldBuffers Buffers { get; }
        public System.Random Random { get; }

        public VoxelInterestGrid Grid { get; internal set; }
        public LineOfSightService Sight { get; internal set; }
        public StealthIndexService Stealth { get; internal set; }
        public SoundEventBus Sound { get; internal set; }
        public AcousticPropagation Acoustics { get; internal set; }
        public InterestManager Interest { get; internal set; }
        public CombatDirector Combat { get; internal set; }
        public DestructibleRegistry Destructibles { get; internal set; }
        public MatchDirector Match { get; internal set; }
        public MistZoneController Mist { get; internal set; }
        public BotDirector Bots { get; internal set; }

        public int Tick { get; internal set; }
        public float Time { get; internal set; }

        public SimContext(UnseenConfig config, Transform root, INetworkService net, int seed)
        {
            Config = config ?? UnseenConfig.Default;
            Root = root;
            Net = net;
            Random = new System.Random(seed);
            Entities = new EntityRegistry(Config.Network.MaxPlayers);
            Buffers = new WorldBuffers(Config.Network.MaxPlayers);
        }

        public void Register<T>(T service) where T : class
        {
            _services[typeof(T)] = service;
        }

        public T Get<T>() where T : class
        {
            return _services.TryGetValue(typeof(T), out object v) ? (T)v : null;
        }
    }

    /// <summary>
    /// Fixed-step authoritative loop. Runs at the combat rate; base-rate systems are gated to
    /// every Nth tick so roaming spatial work stays at 20 Hz while combat pockets resolve at 60 Hz.
    /// </summary>
    public sealed class ServerSimulation : IDisposable
    {
        private const int MaxCatchUpSteps = 4;

        private readonly List<ISimSystem> _systems = new List<ISimSystem>();
        private readonly SimContext _ctx;
        private readonly int _baseDivisor;
        private readonly float _step;

        private float _accumulator;
        private bool _initialized;

        public SimContext Context => _ctx;
        public int Tick { get; private set; }
        public float Time { get; private set; }

        /// <summary>Measured simulation cost of the last stepped frame, in milliseconds.</summary>
        public float LastFrameMilliseconds { get; private set; }

        private long[] _systemTicks = new long[0];
        private int[] _systemCalls = new int[0];

        public ServerSimulation(SimContext ctx)
        {
            _ctx = ctx;
            int combat = Mathf.Max(1, ctx.Config.Network.CombatTickRate);
            int baseRate = Mathf.Clamp(ctx.Config.Network.BaseTickRate, 1, combat);
            _baseDivisor = Mathf.Max(1, Mathf.RoundToInt(combat / (float)baseRate));
            _step = 1f / combat;
        }

        public T Add<T>(T system) where T : ISimSystem
        {
            if (_initialized)
                throw new InvalidOperationException("Systems must be added before Initialize().");
            _systems.Add(system);
            return system;
        }

        public void Initialize()
        {
            _systems.Sort((a, b) => a.Order.CompareTo(b.Order));
            _systemTicks = new long[_systems.Count];
            _systemCalls = new int[_systems.Count];
            foreach (ISimSystem system in _systems)
                system.Initialize(_ctx);
            _initialized = true;
        }

        /// <summary>Feed real elapsed time. Steps zero or more fixed ticks.</summary>
        public void Advance(float deltaTime)
        {
            if (!_initialized) return;

            _accumulator += Mathf.Min(deltaTime, _step * MaxCatchUpSteps);
            int steps = 0;
            while (_accumulator >= _step && steps < MaxCatchUpSteps)
            {
                _accumulator -= _step;
                steps++;
                StepOnce();
            }
        }

        private void StepOnce()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            Tick++;
            Time += _step;
            _ctx.Tick = Tick;
            _ctx.Time = Time;

            bool isBaseTick = Tick % _baseDivisor == 0;
            var frame = new SimFrame(Tick, _step, Time, isBaseTick);

            for (int i = 0; i < _systems.Count; i++)
            {
                ISimSystem system = _systems[i];
                if (system.Rate == SimRate.Base && !isBaseTick) continue;

                long systemStart = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    system.Tick(in frame);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Unseen] {system.Name} threw on tick {Tick}: {e}");
                }

                // Per-system cost, accumulated. Frame time alone says the tick was slow; it does
                // not say which of fourteen systems made it slow, and guessing at that has already
                // cost more than the two counters here.
                _systemTicks[i] += System.Diagnostics.Stopwatch.GetTimestamp() - systemStart;
                _systemCalls[i]++;
            }

            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            LastFrameMilliseconds = (end - start) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>
        /// Total and mean cost of every system since boot, worst first.
        ///
        /// Reported as a string rather than exposed as data because its only consumers are a log
        /// line and a test report.
        /// </summary>
        public string DescribeSystemCost(int top = 6)
        {
            double frequency = System.Diagnostics.Stopwatch.Frequency;
            var order = new List<int>(_systems.Count);
            for (int i = 0; i < _systems.Count; i++) order.Add(i);

            order.Sort((a, b) => _systemTicks[b].CompareTo(_systemTicks[a]));

            var parts = new List<string>(top);
            for (int rank = 0; rank < order.Count && rank < top; rank++)
            {
                int index = order[rank];
                if (_systemCalls[index] == 0) continue;

                double totalMs = _systemTicks[index] * 1000.0 / frequency;
                double meanMs = totalMs / _systemCalls[index];
                parts.Add($"{_systems[index].Name} {totalMs:0} ms total / {meanMs:0.00} ms per tick");
            }

            return parts.Count > 0 ? string.Join(" | ", parts) : "no systems ticked";
        }

        /// <summary>Finds a system by type. For diagnostics and tooling, not for gameplay wiring.</summary>
        public T GetSystem<T>() where T : class, ISimSystem
        {
            for (int i = 0; i < _systems.Count; i++)
                if (_systems[i] is T match)
                    return match;
            return null;
        }

        public void Dispose()
        {
            for (int i = _systems.Count - 1; i >= 0; i--)
            {
                try
                {
                    _systems[i].Shutdown();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Unseen] {_systems[i].Name} threw during shutdown: {e}");
                }
            }

            _systems.Clear();
            _ctx.Buffers.Dispose();
            _ctx.Grid?.Dispose();
            _initialized = false;
        }
    }
}
