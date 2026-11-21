using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>Owns the caldera, connected source shaft and sealed deep chamber on both fills.
/// The ordinary fill/edit hooks do not apply to fresh chunk generation.</summary>
internal static class VolcanoStamp
{
    internal static readonly ushort LavaBlock = ContentRegistry.RequireBlock("lava").Id;
    private static readonly ushort Lining = ContentRegistry.RequireBlock("charred_stone").Id;
    private static readonly ushort Magma = ContentRegistry.RequireBlock("magma_block").Id;

    internal static StampBand WriteBand() => new(
        Math.Min(VolcanoShape.ChamberBottom,
            (int)WorldGenerationConfig.VolcanoMinSummit - WorldGenerationConfig.VolcanoCraterDrop
                - WorldGenerationConfig.VolcanoLakeDepth - WorldGenerationConfig.VolcanoLiningThickness),
        (int)WorldGenerationConfig.VolcanoMaxSummit - WorldGenerationConfig.VolcanoCraterDrop);

    internal static void Stamp(ushort[] blocks, byte[]? state, ColumnData column, int x0, int y0, int z0)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic) return;
        // Every owned XZ point is within 54 blocks of its site center. The chunk-center
        // offset is < 11, so one cone lookup catches it (minimum cone radius 220).
        var site = VolcanoSites.At(x0 + (Chunk.Size - 1) * 0.5f,
            z0 + (Chunk.Size - 1) * 0.5f);
        if (!site.Exists) return;
        var maxX = x0 + Chunk.Size - 1;
        var maxZ = z0 + Chunk.Size - 1;
        var dx = MathF.Max(x0 - site.X, MathF.Max(0f, site.X - maxX));
        var dz = MathF.Max(z0 - site.Z, MathF.Max(0f, site.Z - maxZ));
        var calderaBand = y0 <= site.Floor && y0 + Chunk.Size - 1 >= site.Floor - site.LakeDepth
            - WorldGenerationConfig.VolcanoLiningThickness;
        var calderaBox = calderaBand && dx * dx + dz * dz < site.CraterRadius * site.CraterRadius;
        if (!calderaBox && !VolcanoShape.InDeepBox(site, x0, y0, z0, maxX, y0 + Chunk.Size - 1, maxZ))
            return;
        var first = Math.Max(y0, VolcanoShape.ChamberBottom);
        var last = Math.Min(y0 + Chunk.Size - 1, site.Floor);
        if (first > last) return;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = x0 + x;
            var wz = z0 + z;
            var chamberX = (long)wx - site.Deep.AxisX;
            var chamberZ = (long)wz - site.Deep.AxisZ;
            if (!VolcanoShape.InCrater(site, wx, wz)
                && (!site.Deep.Exists || Math.Abs(chamberX) > VolcanoShape.ChamberReach
                    || Math.Abs(chamberZ) > VolcanoShape.ChamberReach)) continue;
            var surface = column.Heights[(x << Chunk.SizeShift) | z];
            for (var wy = first; wy <= last; wy++)
            {
                var kind = VolcanoShape.Classify(site, wx, wy, wz, surface);
                if (kind == VolcanoCell.None) continue;
                var i = Chunk.Idx(x, wy - y0, z);
                blocks[i] = kind == VolcanoCell.Lava ? LavaBlock : kind == VolcanoCell.Air
                    ? BlockType.Air : wy == surface && !VolcanoShape.InLavaDisc(site, wx, wz)
                    ? Magma : Lining;
                if (state is not null) state[i] = 0;
            }
        }
    }
}
