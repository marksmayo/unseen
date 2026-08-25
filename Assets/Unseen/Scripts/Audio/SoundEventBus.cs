using System.Collections.Generic;
using Unity.Mathematics;
using Unseen.Core;

namespace Unseen.Audio
{
    /// <summary>
    /// Server-side queue of sound spheres raised this tick. Gameplay code only ever emits here;
    /// deciding who heard what is the propagation system's job.
    /// </summary>
    public sealed class SoundEventBus
    {
        private readonly List<SoundEvent> _queue = new List<SoundEvent>(128);
        private readonly List<SoundEvent> _lastTick = new List<SoundEvent>(128);

        public IReadOnlyList<SoundEvent> Queued => _queue;

        /// <summary>Events resolved on the previous tick. Useful for debug draw and replays.</summary>
        public IReadOnlyList<SoundEvent> LastTick => _lastTick;

        public void Emit(SoundEvent e)
        {
            _queue.Add(e);
        }

        public void Emit(AgentId source, float3 position, SoundKind kind, float loudness, float radius, int tick)
        {
            _queue.Add(new SoundEvent
            {
                Source = source,
                Position = position,
                Kind = kind,
                Loudness = loudness,
                Radius = radius,
                Tick = tick
            });
        }

        internal void Swap()
        {
            _lastTick.Clear();
            _lastTick.AddRange(_queue);
            _queue.Clear();
        }

        public void Clear()
        {
            _queue.Clear();
            _lastTick.Clear();
        }
    }
}
