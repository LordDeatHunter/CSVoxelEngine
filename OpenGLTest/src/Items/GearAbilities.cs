using System;

namespace VoxelEngine.Items;

/// <summary>The JSON names and display labels of the <see cref="GearAbility"/> flags (GL-free).</summary>
public static class GearAbilities
{
    /// <summary>Every single ability flag, in display order.</summary>
    public static readonly GearAbility[] All =
        [GearAbility.AirJump, GearAbility.NoFallDamage, GearAbility.ExtraAir, GearAbility.Magnet,
            GearAbility.BoneShrapnel];

    /// <summary>Parses a JSON ability name (snake_case, case-insensitive, trimmed); false with None when unknown.</summary>
    public static bool TryParse(string? text, out GearAbility ability)
    {
        ability = text?.Trim().ToLowerInvariant() switch
        {
            "air_jump" => GearAbility.AirJump,
            "no_fall_damage" => GearAbility.NoFallDamage,
            "extra_air" => GearAbility.ExtraAir,
            "magnet" => GearAbility.Magnet,
            "bone_shrapnel" => GearAbility.BoneShrapnel,
            _ => GearAbility.None,
        };
        return ability != GearAbility.None;
    }

    /// <summary>The snake_case JSON name of one ability flag.</summary>
    public static string Name(GearAbility ability) => ability switch
    {
        GearAbility.AirJump => "air_jump",
        GearAbility.NoFallDamage => "no_fall_damage",
        GearAbility.ExtraAir => "extra_air",
        GearAbility.Magnet => "magnet",
        GearAbility.BoneShrapnel => "bone_shrapnel",
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not a single ability flag."),
    };

    /// <summary>The display text of one ability flag.</summary>
    public static string Label(GearAbility ability) => ability switch
    {
        GearAbility.AirJump => "Double jump",
        GearAbility.NoFallDamage => "No fall damage",
        GearAbility.ExtraAir => "Longer breath",
        GearAbility.Magnet => "Item magnet",
        GearAbility.BoneShrapnel => "Bone shrapnel",
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not a single ability flag."),
    };
}
