using System;

namespace VoxelEngine.Content.Spells;

/// <summary>
/// What an element does to the cells it lands on (its <c>impact</c> list in the element part JSON), beside
/// the status effect it applies to a body. Several flags combine; an element with none touches no cell.
/// </summary>
[Flags]
public enum SpellImpact : byte
{
    /// <summary>No cell is touched.</summary>
    None = 0,

    /// <summary>Burns a <c>flammable</c> block to air.</summary>
    Ignite = 1,

    /// <summary>Sets a fluid source to its <c>freezesTo</c> block.</summary>
    Freeze = 2,

    /// <summary>Melts a block with a <c>meltsTo</c> fluid into a source of it.</summary>
    Thaw = 4,

    /// <summary>Primes a primable block (<c>onBreak: prime_tnt</c>).</summary>
    Prime = 8,

    /// <summary>Lights a fueled kiln or a full branch-pile campfire.</summary>
    Light = 16,

    /// <summary>Grows the aimed tree, or plants the element's <c>plant</c> species on the hit face.</summary>
    Grow = 32,
}
