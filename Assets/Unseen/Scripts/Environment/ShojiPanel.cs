using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Environment
{
    public enum ShojiState : byte
    {
        Intact = 0,

        /// <summary>Cut open. A body fits through, the frame still prints silhouettes.</summary>
        Sliced = 1,

        /// <summary>Torn off the runners. Loud, and no longer conceals anything.</summary>
        Broken = 2
    }

    /// <summary>
    /// A paper screen. Blocks movement and identity but not sound, and prints a silhouette of
    /// anything lit moving behind it. Cutting one is the quiet way in; kicking one through is not.
    /// </summary>
    public sealed class ShojiPanel : MonoBehaviour
    {
        private static readonly List<ShojiPanel> Panels = new List<ShojiPanel>(512);

        [Tooltip("Collider on the ShojiPaper layer. Blocks movement while intact, prints silhouettes.")]
        public Collider PaperCollider;

        [Tooltip("Frame collider on the Default layer. Survives a slice, breaks with the panel.")]
        public Collider FrameCollider;

        [Tooltip("Renderer using the Unseen/ShojiSilhouette shader.")]
        public Renderer PaperRenderer;

        [Tooltip("Loudness of a clean slice. Deliberately low - this is the stealth entry.")]
        public float SliceLoudness = 0.45f;

        public float SliceRadius = 11f;

        [Tooltip("Loudness of tearing the panel down.")]
        public float BreakLoudness = 3.2f;

        public float BreakRadius = 42f;

        public ShojiState State { get; private set; } = ShojiState.Intact;
        public static IReadOnlyList<ShojiPanel> All => Panels;

        public float3 Position => transform.position;
        public float3 Normal => transform.forward;

        private static readonly int SliceProperty = Shader.PropertyToID("_SliceAmount");
        private MaterialPropertyBlock _properties;

        private void OnEnable()
        {
            EnsureRegistered();
        }

        /// <summary>
        /// Wires up references and joins the panel registry. Called from OnEnable in a normal
        /// session, and directly by tools and generators, which run where OnEnable does not.
        /// </summary>
        public void EnsureRegistered()
        {
            if (!Panels.Contains(this)) Panels.Add(this);
            if (PaperCollider == null) PaperCollider = GetComponent<Collider>();
            if (PaperRenderer == null) PaperRenderer = GetComponent<Renderer>();
            if (PaperCollider != null) PaperCollider.gameObject.layer = UnseenLayers.ShojiPaper;
        }

        private void OnDisable()
        {
            Panels.Remove(this);
        }

        /// <summary>Cuts an opening. Returns false if the panel is already open.</summary>
        public bool Slice()
        {
            if (State != ShojiState.Intact) return false;
            State = ShojiState.Sliced;
            if (PaperCollider != null) PaperCollider.enabled = false;
            ApplyVisual(1f);
            return true;
        }

        /// <summary>Tears the whole panel down. Loud, and removes the silhouette surface entirely.</summary>
        public bool Break()
        {
            if (State == ShojiState.Broken) return false;
            State = ShojiState.Broken;
            if (PaperCollider != null) PaperCollider.enabled = false;
            if (FrameCollider != null) FrameCollider.enabled = false;
            if (PaperRenderer != null) PaperRenderer.enabled = false;
            return true;
        }

        public void Restore()
        {
            State = ShojiState.Intact;
            if (PaperCollider != null) PaperCollider.enabled = true;
            if (FrameCollider != null) FrameCollider.enabled = true;
            if (PaperRenderer != null) PaperRenderer.enabled = true;
            ApplyVisual(0f);
        }

        /// <summary>Applies replicated state on a client without re-running the gameplay side effects.</summary>
        public void ApplyReplicatedState(ShojiState state)
        {
            switch (state)
            {
                case ShojiState.Intact:
                    Restore();
                    break;
                case ShojiState.Sliced:
                    Slice();
                    break;
                case ShojiState.Broken:
                    Break();
                    break;
            }
        }

        private void ApplyVisual(float slice)
        {
            if (PaperRenderer == null) return;
            _properties ??= new MaterialPropertyBlock();
            _properties.SetFloat(SliceProperty, slice);
            PaperRenderer.SetPropertyBlock(_properties);
        }

        /// <summary>Nearest panel to a point within range, used to resolve a slice input server-side.</summary>
        public static ShojiPanel NearestIntact(float3 point, float maxDistance)
        {
            ShojiPanel best = null;
            float bestDist = maxDistance * maxDistance;

            for (int i = 0; i < Panels.Count; i++)
            {
                ShojiPanel p = Panels[i];
                if (p == null || p.State != ShojiState.Intact) continue;
                float d = math.distancesq(p.Position, point);
                if (d >= bestDist) continue;
                bestDist = d;
                best = p;
            }

            return best;
        }
    }
}
