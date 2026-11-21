using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Particles;

/// <summary>
/// What one falling leaf wears: the atlas tile it crops and its color. A biome-tinted leaf block
/// (oak, spruce) wears its gray tinted tile with the climate field at the cell it fell from, as its
/// canopy does; any other (crystal) wears its own tile untinted. GL-free; <see cref="LeafParticleRenderer"/>
/// draws it as the vertex rgb and the biome-tint bit, the world shader's convention.
/// </summary>
public readonly record struct LeafLook(ushort Tile, Vector3 Tint, bool Tinted)
{
    private const int TopFace = 4; // +Y in Chunk.CubeFaces order

    /// <summary>
    /// The look of the leaves <paramref name="blockId"/> sheds at <paramref name="cell"/>: a leaf block's own, and
    /// tree wood's its species' leaves (a hit trunk shakes its own crown loose). The tint reads
    /// <paramref name="columns"/> (null means worldgen) and falls back to white on a missing column, like the break
    /// chips (<see cref="BlockDebrisSystem.TintAt"/>). False for a block that sheds no leaves (a leafless species'
    /// wood, any other block).
    /// </summary>
    public static bool TryFor(ushort blockId, Vector3 cell, IColumnSource? columns, out LeafLook look)
    {
        look = default;
        var leafId = TreeGenerator.IsLeaf(blockId) ? blockId : TreeSpeciesRegistry.ForBlock(blockId)?.LeavesId ?? 0;
        if (leafId == 0 || BlockType.ById[leafId] is not { FaceTiles.Length: 6 } type) return false;

        if (type.BiomeTint is { } bt && bt.IsTinted(TopFace))
        {
            look = new LeafLook(bt.Tinted[TopFace], BlockDebrisSystem.TintAt(cell, leafId, columns), true);
            return true;
        }
        look = new LeafLook(type.FaceTiles[TopFace], Vector3.One, false);
        return true;
    }
}
