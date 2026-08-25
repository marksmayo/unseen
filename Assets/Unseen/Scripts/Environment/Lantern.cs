using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Perception;

namespace Unseen.Environment
{
    /// <summary>
    /// A hanging light that can be put out. Killing a lantern is an offensive act: it grows the
    /// shadow pocket you are standing in, and everyone nearby hears the paper and glass go.
    /// </summary>
    [RequireComponent(typeof(StealthLightSource))]
    public sealed class Lantern : MonoBehaviour
    {
        private static readonly List<Lantern> Lanterns = new List<Lantern>(256);

        public StealthLightSource Source;

        [Tooltip("Hit points. A shuriken or a swing takes one out.")]
        public float Health = 1f;

        public float BreakLoudness = 1.8f;
        public float BreakRadius = 34f;

        [Tooltip("Optional debris/particle prefab spawned when the lantern is destroyed.")]
        public GameObject BreakEffect;

        public bool IsLit => Source != null && !Source.Extinguished;
        public static IReadOnlyList<Lantern> All => Lanterns;
        public float3 Position => transform.position;

        private void OnEnable()
        {
            EnsureRegistered();
        }

        /// <summary>Joins the lantern registry. Safe to call more than once.</summary>
        public void EnsureRegistered()
        {
            if (!Lanterns.Contains(this)) Lanterns.Add(this);
            if (Source == null) Source = GetComponent<StealthLightSource>();
        }

        private void OnDisable()
        {
            Lanterns.Remove(this);
        }

        /// <summary>Returns true when this hit actually put the lantern out.</summary>
        public bool Extinguish(float damage = 1f)
        {
            if (Source == null || Source.Extinguished) return false;

            Health -= damage;
            if (Health > 0f) return false;

            Source.SetExtinguished(true);
            if (BreakEffect != null) Instantiate(BreakEffect, transform.position, transform.rotation);
            return true;
        }

        public void Relight()
        {
            Health = Mathf.Max(Health, 1f);
            Source?.SetExtinguished(false);
        }

        public static Lantern NearestLit(float3 point, float maxDistance)
        {
            Lantern best = null;
            float bestDist = maxDistance * maxDistance;

            for (int i = 0; i < Lanterns.Count; i++)
            {
                Lantern l = Lanterns[i];
                if (l == null || !l.IsLit) continue;
                float d = math.distancesq(l.Position, point);
                if (d >= bestDist) continue;
                bestDist = d;
                best = l;
            }

            return best;
        }
    }
}
