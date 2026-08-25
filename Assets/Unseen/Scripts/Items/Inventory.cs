using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.Items
{
    public struct ItemStack
    {
        public ItemDefinition Item;
        public int Count;
    }

    /// <summary>
    /// Loadout for one agent. Gear contributions are folded into cached multipliers so the
    /// perception and audio systems read a single float instead of walking the item list.
    /// </summary>
    public sealed class Inventory : MonoBehaviour
    {
        public const int MaxGear = 3;
        public const int MaxUtility = 3;

        [SerializeField] private ItemDefinition _weapon;
        [SerializeField] private List<ItemDefinition> _gear = new List<ItemDefinition>(MaxGear);
        [SerializeField] private List<ItemStack> _utility = new List<ItemStack>(MaxUtility);

        public ItemDefinition Weapon => _weapon;
        public IReadOnlyList<ItemDefinition> Gear => _gear;
        public IReadOnlyList<ItemStack> Utility => _utility;

        /// <summary>Sum of gear and weapon stealth modifiers. Read by the stealth service.</summary>
        public float StealthBonus { get; private set; }

        public float FootstepLoudnessScale { get; private set; } = 1f;
        public float FootstepRadiusScale { get; private set; } = 1f;

        /// <summary>True while a night-vision elixir is active. Extends effective sight range.</summary>
        public bool HasNightVision { get; private set; }

        private float _nightVisionExpiry = float.NegativeInfinity;

        public float DamageScale => _weapon != null ? _weapon.DamageScale : 1f;
        public float ReachBonus => _weapon != null ? _weapon.ReachBonus : 0f;
        public float WindupBonus => _weapon != null ? _weapon.WindupBonus : 0f;
        public WeaponClass WeaponClass => _weapon != null ? _weapon.Weapon : WeaponClass.None;

        public bool TryAdd(ItemDefinition item)
        {
            if (item == null) return false;

            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    if (_weapon != null && _weapon.DamageScale >= item.DamageScale) return false;
                    _weapon = item;
                    Recompute();
                    return true;

                case ItemKind.Gear:
                    if (_gear.Contains(item)) return false;
                    if (_gear.Count >= MaxGear) return false;
                    _gear.Add(item);
                    Recompute();
                    return true;

                default:
                    for (int i = 0; i < _utility.Count; i++)
                    {
                        if (_utility[i].Item != item) continue;
                        if (_utility[i].Count >= item.StackSize) return false;
                        _utility[i] = new ItemStack { Item = item, Count = _utility[i].Count + 1 };
                        return true;
                    }

                    if (_utility.Count >= MaxUtility) return false;
                    _utility.Add(new ItemStack { Item = item, Count = 1 });
                    return true;
            }
        }

        public bool HasUtility(UtilityEffect effect)
        {
            for (int i = 0; i < _utility.Count; i++)
                if (_utility[i].Item != null && _utility[i].Item.Effect == effect && _utility[i].Count > 0)
                    return true;
            return false;
        }

        /// <summary>Removes one charge of the first utility matching the effect and returns its definition.</summary>
        public ItemDefinition ConsumeUtility(UtilityEffect effect)
        {
            for (int i = 0; i < _utility.Count; i++)
            {
                ItemStack stack = _utility[i];
                if (stack.Item == null || stack.Item.Effect != effect || stack.Count <= 0) continue;

                stack.Count--;
                if (stack.Count <= 0) _utility.RemoveAt(i);
                else _utility[i] = stack;
                return stack.Item;
            }

            return null;
        }

        public ItemDefinition ConsumeUtilitySlot(int index)
        {
            if (index < 0 || index >= _utility.Count) return null;
            ItemStack stack = _utility[index];
            if (stack.Item == null || stack.Count <= 0) return null;

            stack.Count--;
            if (stack.Count <= 0) _utility.RemoveAt(index);
            else _utility[index] = stack;
            return stack.Item;
        }

        public void ApplyNightVision(float now, float duration)
        {
            _nightVisionExpiry = now + duration;
            HasNightVision = true;
        }

        internal void TickEffects(float now)
        {
            HasNightVision = now < _nightVisionExpiry;
        }

        public void Clear()
        {
            _weapon = null;
            _gear.Clear();
            _utility.Clear();
            _nightVisionExpiry = float.NegativeInfinity;
            HasNightVision = false;
            Recompute();
        }

        private void Recompute()
        {
            float stealth = 0f;
            float loudness = 1f;
            float radius = 1f;

            if (_weapon != null)
            {
                stealth += _weapon.StealthBonus;
                loudness *= _weapon.FootstepLoudnessScale;
                radius *= _weapon.FootstepRadiusScale;
            }

            for (int i = 0; i < _gear.Count; i++)
            {
                ItemDefinition g = _gear[i];
                if (g == null) continue;
                stealth += g.StealthBonus;
                loudness *= g.FootstepLoudnessScale;
                radius *= g.FootstepRadiusScale;
            }

            StealthBonus = Mathf.Clamp(stealth, -0.5f, 0.5f);
            FootstepLoudnessScale = Mathf.Clamp(loudness, 0.05f, 3f);
            FootstepRadiusScale = Mathf.Clamp(radius, 0.05f, 3f);
        }

        private void OnValidate()
        {
            Recompute();
        }
    }

    /// <summary>Expires timed item effects and refreshes environmental flags such as smoke cover.</summary>
    public sealed class AgentEffectsSystem : SimSystem
    {
        public override int Order => SimOrder.Environment;
        public override SimRate Rate => SimRate.Base;

        public override void Tick(in SimFrame frame)
        {
            EntityRegistry registry = Ctx.Entities;
            for (int i = 0; i < registry.Count; i++)
            {
                AgentEntity agent = registry.BySlot(i);
                if (agent.Inventory != null) agent.Inventory.TickEffects(frame.Time);

                bool smoked = SmokeCloud.Covers(agent.TorsoPosition);
                if (smoked) agent.Flags |= AgentFlags.Smoked;
                else agent.Flags &= ~AgentFlags.Smoked;
            }
        }
    }
}
