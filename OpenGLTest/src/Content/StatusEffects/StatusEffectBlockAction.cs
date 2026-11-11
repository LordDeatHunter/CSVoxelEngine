using System;

namespace VoxelEngine.Content.StatusEffects;

/// <summary>What a status effect's tick does to the cells around its target, as data flags.</summary>
[Flags]
public enum StatusEffectBlockAction : byte
{
    None = 0,

    /// <summary>A flammable block burns away to air.</summary>
    Ignite = 1,

    /// <summary>A fluid source below the target's feet freezes to its <c>freezesTo</c> block.</summary>
    Freeze = 2,

    /// <summary>A block with a <c>meltsTo</c> fluid melts into a source of it.</summary>
    Thaw = 4,
}
