using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>Owns every S5a caldera lava, wall and bed cell on both normal and lake-probe fills.
/// The ordinary fill/edit hooks do not apply to fresh chunk generation.</summary>
internal static class VolcanoStamp
{
    internal static readonly ushort LavaBlock = ContentRegistry.RequireBlock("lava").Id;
    private static readonly ushort Lining = ContentRegistry.RequireBlock("charred_stone").Id;
    private static readonly ushort Magma = ContentRegistry.RequireBlock("magma_block").Id;

    internal static StampBand WriteBand() => new(
        (int)WorldGenerationConfig.VolcanoMinSummit - WorldGenerationConfig.VolcanoCraterDrop
            - WorldGenerationConfig.VolcanoLakeDepth - WorldGenerationConfig.VolcanoLiningThickness,
        (int)WorldGenerationConfig.VolcanoMaxSummit - WorldGenerationConfig.VolcanoCraterDrop);

    internal static void Stamp(ushort[] blocks, byte[]? state, ColumnData column, int x0, int y0, int z0)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = x0 + x;
            var wz = z0 + z;
            var site = VolcanoSites.At(wx, wz);
            if (!VolcanoShape.InCrater(site, wx, wz)) continue;
            var surface = column.Heights[(x << Chunk.SizeShift) | z];
            var first = Math.Max(y0, surface - site.LakeDepth - WorldGenerationConfig.VolcanoLiningThickness);
            var last = Math.Min(y0 + Chunk.Size - 1, surface);
            for (var wy = first; wy <= last; wy++)
            {
                var kind = VolcanoShape.Classify(site, wx, wy, wz, surface);
                if (kind == VolcanoCell.None) continue;
                var i = Chunk.Idx(x, wy - y0, z);
                blocks[i] = kind == VolcanoCell.Lava ? LavaBlock
                    : wy == surface && !VolcanoShape.InLavaDisc(site, wx, wz) ? Magma : Lining;
                if (state is not null) state[i] = 0;
            }
        }
    }
}
