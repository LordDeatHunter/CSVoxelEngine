using System.Collections.Generic;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// What a battle tower is built of, by the biome it stands in: the unbreakable tower brick of its palette (sandstone in the
/// desert, frost in the taiga and the frozen plains, plain elsewhere) and the breakable half-step the stair alternates with
/// it. A palette swap only: the shell's shape never depends on it. Resolved once, after content is loaded.
/// </summary>
/// <param name="Brick">The unbreakable tower brick every wall, slab and the foundation is made of.</param>
/// <param name="Step">The breakable slab every even stair half-step is made of.</param>
public sealed record TowerPalette(byte Brick, byte Step)
{
    private static readonly TowerPalette Plain;
    private static readonly TowerPalette Sandstone;
    private static readonly TowerPalette Frost;

    static TowerPalette()
    {
        var step = ContentRegistry.RequireBlock("cobblestone_slab").Id;
        DefaultBrick = ContentRegistry.RequireBlock("tower_bricks").Id;
        StoopBlock = ContentRegistry.RequireBlock("cobblestone").Id;
        DoorBlock = ContentRegistry.RequireBlock("tower_door").Id;
        PileBlock = ContentRegistry.RequireBlock("skeleton_pile").Id;
        Plain = new TowerPalette(DefaultBrick, step);
        Sandstone = new TowerPalette(ContentRegistry.RequireBlock("tower_bricks_sandstone").Id, step);
        Frost = new TowerPalette(ContentRegistry.RequireBlock("tower_bricks_frost").Id, step);
        All = [Plain, Sandstone, Frost];
    }

    /// <summary>The plain tower brick, which a dressing template names and the stamp swaps for the site's own brick.</summary>
    internal static byte DefaultBrick { get; }

    /// <summary>The block under the entrance apron's walkway.</summary>
    internal static byte StoopBlock { get; }

    /// <summary>
    /// The unbreakable gate worldgen stamps across each floor's stairwell, the same in every palette; the tower's lock
    /// clears it once the floor below is cleared.
    /// </summary>
    internal static byte DoorBlock { get; }

    /// <summary>
    /// The unbreakable bone heap a floor template places, the same in every palette; its skeletons rise from it, and the
    /// tower's lock clears it once it is spent.
    /// </summary>
    internal static byte PileBlock { get; }

    /// <summary>Every palette, once each.</summary>
    internal static IReadOnlyList<TowerPalette> All { get; }

    /// <summary>The palette of a tower whose center stands in <paramref name="biome"/>.</summary>
    internal static TowerPalette For(Biome biome) => biome switch
    {
        Biome.Desert => Sandstone,
        Biome.Frozen or Biome.Taiga => Frost,
        _ => Plain
    };
}
