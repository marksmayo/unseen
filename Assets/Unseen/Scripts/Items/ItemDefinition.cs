using UnityEngine;

namespace Unseen.Items
{
    public enum ItemKind : byte
    {
        Weapon = 0,
        Gear = 1,
        Utility = 2,
        Consumable = 3
    }

    public enum WeaponClass : byte
    {
        None = 0,

        /// <summary>Balanced reach and speed. The default clash weapon.</summary>
        Katana = 1,

        /// <summary>Long reach, slow recovery, can catch a guard from outside katana range.</summary>
        Kusarigama = 2,

        /// <summary>Thrown, silent, low damage. Punishes a distracted enemy, never a ready one.</summary>
        Shuriken = 3
    }

    public enum UtilityEffect : byte
    {
        None = 0,
        SmokeBomb = 1,
        Noisemaker = 2,
        NightVisionElixir = 3
    }

    /// <summary>
    /// One lootable thing. Everything an item does to an agent is expressed here as a modifier,
    /// so the stealth, acoustic and combat systems never special-case a particular item.
    /// </summary>
    [CreateAssetMenu(menuName = "Unseen/Item", fileName = "Item")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string Id = "item";
        public string DisplayName = "Item";
        public ItemKind Kind = ItemKind.Utility;

        [Tooltip("How many of this item one inventory slot holds.")]
        public int StackSize = 1;

        [Header("Weapon")]
        public WeaponClass Weapon = WeaponClass.None;
        public float DamageScale = 1f;
        public float ReachBonus;
        public float SwingLoudness = 1.4f;
        public float SwingRadius = 18f;

        [Tooltip("Seconds added to the wind-up. Heavier weapons telegraph more.")]
        public float WindupBonus;

        [Header("Stealth modifiers")]
        [Tooltip("Added directly to the stealth index while equipped.")]
        [Range(-0.5f, 0.5f)] public float StealthBonus;

        [Tooltip("Multiplier on footstep loudness. Tabi boots sit around 0.5.")]
        [Range(0f, 2f)] public float FootstepLoudnessScale = 1f;

        [Tooltip("Multiplier on footstep audible radius.")]
        [Range(0f, 2f)] public float FootstepRadiusScale = 1f;

        [Header("Utility")]
        public UtilityEffect Effect = UtilityEffect.None;
        public float EffectRadius = 6f;
        public float EffectDuration = 8f;
        public float ThrowSpeed = 16f;

        [Tooltip("Loudness of the effect when it triggers. Smoke is quiet, a noisemaker is not.")]
        public float EffectLoudness = 1f;

        public float EffectSoundRadius = 30f;

        public bool IsWeapon => Kind == ItemKind.Weapon;
        public bool IsGear => Kind == ItemKind.Gear;
    }
}
