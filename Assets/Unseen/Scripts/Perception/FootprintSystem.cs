using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.Perception
{
    /// <summary>
    /// Presses marks into raked gravel as bodies cross it, and fills them back in over time.
    ///
    /// Runs on the server so that everybody sees the same trail. A print is evidence - somebody
    /// arriving at a garden should be able to read which way the last person went - and evidence
    /// that each client invented for itself would be evidence that disagreed.
    ///
    /// Base rate rather than combat rate. A stride is most of a second even at a sprint, and
    /// nothing here needs resolving at sixty hertz.
    /// </summary>
    public sealed class FootprintSystem : SimSystem
    {
        // One past DrowningSystem rather than level with it. They do not interact, but the sort
        // that orders systems is not a stable one, so two systems sharing a number sit in whichever
        // order the sort happened to leave them - and that can change because something elsewhere
        // in the list moved. A tick order that depends on an unrelated edit is a trap, not a
        // design.
        public override int Order => SimOrder.Mist - 14;
        public override SimRate Rate => SimRate.Base;

        /// <summary>Where each agent last left a mark, so prints are spaced by stride not by tick.</summary>
        private readonly Dictionary<int, float3> _lastPrint = new Dictionary<int, float3>(64);

        /// <summary>Which foot is next. A line of prints on one side is a hopping man.</summary>
        private readonly Dictionary<int, bool> _leftFoot = new Dictionary<int, bool>(64);

        /// <summary>Marks pressed since boot. Watched by the probes.</summary>
        public int Pressed { get; private set; }

        public override void Tick(in SimFrame frame)
        {
            UnseenConfig.GravelSection cfg = Ctx.Config.Gravel;

            // Fading happens whether or not anybody is walking, and whether or not the feature is
            // switched on - otherwise turning it off mid-match would freeze existing prints in the
            // ground for ever.
            Footprints.Advance(frame.Dt);

            if (!cfg.Enabled || GravelBed.All.Count == 0) return;

            EntityRegistry registry = Ctx.Entities;

            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent == null || !agent.IsAlive) continue;

                GravelBed bed = GravelBed.At(agent.Position);

                if (bed == null)
                {
                    // Forget where they last trod the moment they leave, so stepping back onto a
                    // bed starts a fresh line instead of measuring a stride across the whole town.
                    _lastPrint.Remove(agent.Id.Value);
                    continue;
                }

                bool crawling = agent.Stance == Stance.Prone;

                // A crawling body is in continuous contact, so its marks are spaced much closer
                // than a walker's stride - it leaves a trough, not a series of steps.
                float spacing = crawling ? cfg.CrawlSpacing : cfg.StrideLength;

                if (_lastPrint.TryGetValue(agent.Id.Value, out float3 last) &&
                    math.lengthsq(UnseenMath.Horizontal(agent.Position - last)) <
                    spacing * spacing)
                    continue;

                _lastPrint[agent.Id.Value] = agent.Position;

                float3 forward = math.normalizesafe(
                    UnseenMath.Horizontal(agent.Motor != null
                        ? (float3)agent.Motor.Velocity
                        : agent.Forward),
                    agent.Forward);

                float3 at = new float3(agent.Position.x, bed.SurfaceY, agent.Position.z);

                if (!crawling)
                {
                    // Offset to one side, alternating. Prints down the centre line read as a
                    // machine having rolled through rather than as somebody having walked.
                    _leftFoot.TryGetValue(agent.Id.Value, out bool left);
                    _leftFoot[agent.Id.Value] = !left;

                    float3 side = math.normalizesafe(
                        math.cross(new float3(0f, 1f, 0f), forward), new float3(1f, 0f, 0f));

                    at += side * (left ? -cfg.FootSpacing : cfg.FootSpacing);
                }

                Footprints.Press(at, forward, crawling, agent.Id,
                    crawling ? cfg.CrawlLife : cfg.PrintLife, GravelMaterial(bed));

                Pressed++;
            }
        }

        /// <summary>
        /// The bed's own material, so a scuff is made of the same stone the garden is.
        ///
        /// Taken off the renderer rather than from the material set, because a bed placed by hand
        /// in a scene may not be using the generated gravel at all.
        /// </summary>
        private static Material GravelMaterial(GravelBed bed)
        {
            var renderer = bed.GetComponent<Renderer>();
            return renderer != null ? renderer.sharedMaterial : null;
        }

        public override void Shutdown()
        {
            _lastPrint.Clear();
            _leftFoot.Clear();
            Footprints.ClearAll();
            Pressed = 0;
        }
    }
}
