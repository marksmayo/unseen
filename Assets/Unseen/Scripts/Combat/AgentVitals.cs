using System;
using UnityEngine;
using Unseen.Core;

namespace Unseen.Combat
{
    /// <summary>
    /// Health for one agent. There is no regeneration and no downed state: this game is decided by
    /// who saw whom first, so a fight that lands cleanly ends.
    /// </summary>
    public sealed class AgentVitals : MonoBehaviour
    {
        [SerializeField] private float _maxHealth = 100f;

        public float Health { get; private set; }
        public float MaxHealth => _maxHealth;
        public float Fraction => _maxHealth <= 0f ? 0f : Mathf.Clamp01(Health / _maxHealth);
        public bool IsDead => Health <= 0f;

        /// <summary>Simulation time of the most recent damage taken. Drives the "under attack" AI reflex.</summary>
        public float LastDamageTime { get; private set; } = float.NegativeInfinity;

        public AgentId LastAttacker { get; private set; }

        /// <summary>Raised on the server when health crosses zero. Carries the killing blow.</summary>
        public event Action<DamageInfo> Died;

        private void Awake()
        {
            if (Health <= 0f) Health = _maxHealth;
        }

        public void Configure(float maxHealth)
        {
            _maxHealth = Mathf.Max(1f, maxHealth);
            Health = _maxHealth;
        }

        public void ResetVitals()
        {
            Health = _maxHealth;
            LastDamageTime = float.NegativeInfinity;
            LastAttacker = AgentId.None;
        }

        /// <summary>Applies damage. Returns true when this blow was fatal.</summary>
        public bool Apply(in DamageInfo info, float now)
        {
            if (IsDead) return false;

            Health = Mathf.Max(0f, Health - Mathf.Max(0f, info.Amount));
            LastDamageTime = now;
            if (info.Attacker.IsValid) LastAttacker = info.Attacker;

            if (Health > 0f) return false;

            Died?.Invoke(info);
            return true;
        }

        public void SetHealthDirect(float value)
        {
            Health = Mathf.Clamp(value, 0f, _maxHealth);
        }
    }
}
