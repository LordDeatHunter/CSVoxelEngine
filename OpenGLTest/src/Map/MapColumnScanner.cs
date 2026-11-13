using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Map;

/// <summary>
/// Scans one chunk column into a <see cref="MapTile"/> for a <see cref="MapLayer"/>: each world column is walked down
/// from the layer's top to the first map-visible cell (<see cref="MapColors"/>), skipping all-air and unloaded chunks
/// whole, and painted with that block's color under its biome tint. A fluid darkens with the depth down to its floor;
/// a cave slice that starts inside rock reads as rock, and one that finds no floor as a pit; a cave floor darkens with
/// its depth under the slice top. Last, every texel is lit or shaded by how far it stands above or below its northern
/// neighbor (the row north of the tile comes from <c>north</c> when that tile exists), the relief a paper map draws.
/// A column that found nothing because a chunk on its way was unloaded stays unexplored (alpha 0). GL-free; main
/// thread only (one shared scratch).
/// </summary>
public static class MapColumnScanner
{
    // The open sky under the islands, where a sky column finds no island
    private static readonly (byte R, byte G, byte B) SkyVoidColor = (22, 30, 46);

    private static readonly Vector3[] Linear = new Vector3[MapTile.TexelCount];
    private static readonly bool[] Relief = new bool[MapTile.TexelCount];
    private static MapChunkState[] _states = [];

    /// <summary>Fills <paramref name="into"/> with chunk column (<paramref name="into"/>.ChunkX, ChunkZ) seen through
    /// <paramref name="layer"/>; <paramref name="north"/> is the same layer's tile north of it, or null.</summary>
    public static void Scan(IMapCells cells, MapLayer layer, MapTile into, MapTile? north)
    {
        var bottomCy = layer.BottomY >> Chunk.SizeShift;
        var topCy = layer.TopY >> Chunk.SizeShift;
        var span = topCy - bottomCy + 1;
        if (_states.Length < span) _states = new MapChunkState[span];
        for (var cy = bottomCy; cy <= topCy; cy++)
            _states[cy - bottomCy] = cells.StateOf(into.ChunkX, cy, into.ChunkZ);

        BiomeTintFields? fields = null;
        var tintChecked = false;
        var baseX = into.ChunkX << Chunk.SizeShift;
        var baseZ = into.ChunkZ << Chunk.SizeShift;
        for (var lz = 0; lz < MapTile.Size; lz++)
        for (var lx = 0; lx < MapTile.Size; lx++)
        {
            var i = MapTile.IndexOf(lx, lz);
            Relief[i] = false;
            var column = ScanColumn(cells, layer, bottomCy, baseX + lx, baseZ + lz);
            into.Heights[i] = column.Height;
            if (column.Texel is { } texel)
            {
                into.Texels[i] = texel;
                continue;
            }

            var color = MapColors.ColorOf(column.Block);
            if (MapColors.PaletteOf(column.Block) is { } palette)
            {
                if (!tintChecked)
                {
                    tintChecked = true;
                    fields = cells.TryTintFields(into.ChunkX, into.ChunkZ, out var found) ? found : null;
                }
                if (fields is { } f)
                    color *= f.Field(palette).Sample(lx + 0.5f, lz + 0.5f) * BiomeTint.Gain;
            }
            Linear[i] = color * column.Shade;
            Relief[i] = true;
        }

        for (var lz = 0; lz < MapTile.Size; lz++)
        for (var lx = 0; lx < MapTile.Size; lx++)
        {
            var i = MapTile.IndexOf(lx, lz);
            if (!Relief[i]) continue;
            var northHeight = lz > 0 ? into.Heights[i - MapTile.Size]
                : north?.Heights[MapTile.IndexOf(lx, MapTile.Size - 1)] ?? MapTile.NoHeight;
            into.Texels[i] = MapColors.Pack(Linear[i] * ReliefShade(into.Heights[i], northHeight));
        }
    }

    /// <summary>The brightness factor of a column at <paramref name="height"/> beside a northern neighbor at
    /// <paramref name="northHeight"/>: brighter standing above it, darker below, capped; 1 next to an unknown one.</summary>
    public static float ReliefShade(short height, short northHeight)
    {
        if (northHeight == MapTile.NoHeight || height == MapTile.NoHeight) return 1f;
        var steps = Math.Clamp(height - northHeight, -MapConfig.ReliefMaxSteps, MapConfig.ReliefMaxSteps);
        return 1f + steps * MapConfig.ReliefStep;
    }

    // One column's find: a block to paint (with its shade) or a ready texel (rock, pit, sky void, unexplored)
    private readonly record struct ColumnFind(ushort Block, short Height, float Shade, uint? Texel);

    private static ColumnFind ScanColumn(IMapCells cells, MapLayer layer, int bottomCy, int x, int z)
    {
        var sawUnloaded = false;
        var y = layer.TopY;
        while (y >= layer.BottomY)
        {
            var cy = y >> Chunk.SizeShift;
            var state = _states[cy - bottomCy];
            if (state != MapChunkState.Blocks)
            {
                sawUnloaded |= state == MapChunkState.Unloaded;
                y = (cy << Chunk.SizeShift) - 1;
                continue;
            }

            var id = cells.BlockAt(x, y, z);
            var kind = MapColors.KindOf(id);
            if (kind == MapCellKind.Clear)
            {
                y--;
                continue;
            }
            if (layer.Kind == MapLayerKind.Cave && y == layer.TopY && kind == MapCellKind.Solid)
                return new ColumnFind(BlockType.Air, (short)y, 1f, MapColors.Pack(MapConfig.RockColor));

            var shade = kind == MapCellKind.Fluid ? FluidShade(cells, layer, bottomCy, x, y, z) : 1f;
            if (layer.Kind == MapLayerKind.Cave)
                shade *= MathF.Max(MapConfig.CaveMinShade, 1f - (layer.TopY - y) * MapConfig.CaveDepthDarken);
            return new ColumnFind(id, (short)y, shade, null);
        }

        if (sawUnloaded) return new ColumnFind(BlockType.Air, MapTile.NoHeight, 1f, 0u);
        var empty = layer.Kind == MapLayerKind.Sky ? SkyVoidColor : MapConfig.PitColor;
        return new ColumnFind(BlockType.Air, MapTile.NoHeight, 1f, MapColors.Pack(empty));
    }

    // A fluid's darkening: one step per fluid cell under its surface, down to the floor or the depth cap
    private static float FluidShade(IMapCells cells, MapLayer layer, int bottomCy, int x, int surfaceY, int z)
    {
        var depth = 0;
        for (var y = surfaceY - 1; y >= layer.BottomY && depth < MapConfig.MaxFluidDepth; y--)
        {
            if (_states[(y >> Chunk.SizeShift) - bottomCy] != MapChunkState.Blocks) break;
            if (MapColors.KindOf(cells.BlockAt(x, y, z)) != MapCellKind.Fluid) break;
            depth++;
        }
        return MathF.Max(MapConfig.FluidMinShade, 1f - depth * MapConfig.FluidDepthDarken);
    }
}
