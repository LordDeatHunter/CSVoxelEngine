namespace VoxelEngine.Items;

/// <summary>The stats a piece of gear may change. Armor, max health and max mana are flat; the rest are
/// fractions added to 1 (0.1 = 10% more), <see cref="UndeadDamage"/> being the extra damage dealt to undead mobs.</summary>
public enum GearStat : byte
{
    Armor,
    MaxHealth,
    MaxMana,
    ManaRegen,
    MoveSpeed,
    MiningSpeed,
    AttackSpeed,
    AttackDamage,
    UndeadDamage,
}
