namespace VoxelEngine.Config;

/// <summary>Gear and equipment tuning: how many pieces a player carries and wears, and how armor reduces damage.</summary>
public static class GearConfig
{
    /// <summary>Gear instances a player can carry (their own pool, apart from the 8-slot tools pool).</summary>
    public const int GearCapacity = 64;

    /// <summary>Armor slots: head, chest, legs, feet, gloves, in that order.</summary>
    public const int ArmorSlotCount = 5;

    /// <summary>Accessory slots; rings, charms, amulets and belts all share them.</summary>
    public const int AccessorySlotCount = 4;

    /// <summary>Every equipment slot: the armor slots, then the accessory slots.</summary>
    public const int EquipmentSlotCount = ArmorSlotCount + AccessorySlotCount;

    /// <summary>The most extra max health gear may grant, so a status packet can bound it.</summary>
    public const int MaxHealthBonusCap = 300;

    /// <summary>The most extra max mana gear may grant.</summary>
    public const int MaxManaBonusCap = 300;

    /// <summary>The fractional bonuses (speed, regen, mining, attack) are clamped to this much more than normal,
    /// and the speed penalties to this much less.</summary>
    public const float FactorBonusCap = 1f;

    /// <summary>Armor points cap: a worn set can never reduce a blow by more than this.</summary>
    public const int ArmorCap = 60;

    /// <summary>How many modifiers loot gear rolls at each rarity, common to legendary.</summary>
    public static readonly int[] ModifiersPerRarity = [0, 1, 2, 3, 4];

    /// <summary>Loot weight of each rarity, common to legendary: the rarer, the less likely.</summary>
    public static readonly int[] LootWeightPerRarity = [60, 25, 10, 4, 1];

    /// <summary>How far from the wearer an on-damaged or low-health passive reaches, in blocks.</summary>
    public const float PassiveAuraRadiusBlocks = 5f;

    /// <summary>Seconds a low-health passive waits after it fired.</summary>
    public const float PassiveLowHealthCooldownSeconds = 8f;

    /// <summary>Minimum damage a blow keeps through armor when its source names no value of its own.</summary>
    public const int UniversalMinDamage = 1;

    /// <summary>How many times a full lung the extra_air ability holds (the gill charm).</summary>
    public const float ExtraAirFactor = 2f;

    /// <summary>How many times <c>DropManager.PickupRange</c> the magnet ability attracts drops from (the lodestone charm).</summary>
    public const float MagnetRangeFactor = 3f;
}
