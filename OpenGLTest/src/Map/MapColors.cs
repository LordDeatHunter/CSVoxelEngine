using System;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Graphics;

namespace VoxelEngine.Map;

/// <summary>How the map treats a block: seen through, a solid top, or a fluid it looks down into.</summary>
public enum MapCellKind : byte
{
    Clear,
    Solid,
    Fluid,
}

/// <summary>
/// Each block's map color: the mean of its top face's atlas tile (<see cref="TextureAtlas.TileMeanLinear"/>), in
/// linear RGB. A block whose top is biome tinted (grass, leaves) keeps its tinted tile's mean and the palette, and
/// the scanner multiplies in the column's tint as the mesh does. Air and everything neither solid nor fluid (plants,
/// torches) is <see cref="MapCellKind.Clear"/>: the map shows the ground under it. Rebuilt whenever the block
/// registry or the atlas is replaced (a new content load), so tests and the game always read the live content. GL-free.
/// </summary>
public static class MapColors
{
    private static BlockType?[]? _builtFor;
    private static int _builtLayers;
    private static Vector3[] _color = [];
    private static MapCellKind[] _kind = [];
    private static BiomeTintPalette?[] _palette = [];

    /// <summary>How the map treats block <paramref name="id"/>.</summary>
    public static MapCellKind KindOf(ushort id)
    {
        EnsureBuilt();
        return id < _kind.Length ? _kind[id] : MapCellKind.Clear;
    }

    /// <summary>Block <paramref name="id"/>'s top color in linear RGB, before any biome tint.</summary>
    public static Vector3 ColorOf(ushort id)
    {
        EnsureBuilt();
        return id < _color.Length ? _color[id] : Vector3.Zero;
    }

    /// <summary>The palette block <paramref name="id"/>'s top is tinted by, or null when it is drawn as is.</summary>
    public static BiomeTintPalette? PaletteOf(ushort id)
    {
        EnsureBuilt();
        return id < _palette.Length ? _palette[id] : null;
    }

    private static void EnsureBuilt()
    {
        var blocks = BlockType.ById;
        if (ReferenceEquals(blocks, _builtFor) && _builtLayers == TextureAtlas.LayerCount) return;

        var means = TextureAtlas.TileMeanLinear;
        var color = new Vector3[blocks.Length];
        var kind = new MapCellKind[blocks.Length];
        var palette = new BiomeTintPalette?[blocks.Length];
        for (var id = 0; id < blocks.Length; id++)
        {
            if (blocks[id] is not { } type || id == BlockType.Air) continue;
            kind[id] = BlockType.IsFluid((ushort)id) ? MapCellKind.Fluid
                : BlockType.IsSolid((ushort)id) ? MapCellKind.Solid
                : MapCellKind.Clear;
            if (kind[id] == MapCellKind.Clear) continue;

            var tile = type.FaceTiles.Length > 4 ? type.FaceTiles[4] : BiomeTintFaces.NoTile;
            if (type.BiomeTint is { } tint && tint.Tinted[4] != BiomeTintFaces.NoTile)
            {
                tile = tint.Tinted[4];
                palette[id] = tint.Palette;
            }
            color[id] = tile < means.Count ? means[tile] : Vector3.Zero;
        }

        _color = color;
        _kind = kind;
        _palette = palette;
        _builtFor = blocks;
        _builtLayers = TextureAtlas.LayerCount;
    }

    /// <summary>Packs a linear color as the sRGB RGBA bytes a map tile stores (little-endian R first, see
    /// <c>Rgba.Pack</c>).</summary>
    public static uint Pack(Vector3 linear)
    {
        var srgb = ColorSpace.LinearToSrgb(Vector3.Clamp(linear, Vector3.Zero, Vector3.One));
        return (uint)(ToByte(srgb.X) | (ToByte(srgb.Y) << 8) | (ToByte(srgb.Z) << 16) | (255 << 24));
    }

    /// <summary>Packs an sRGB byte triple as an opaque tile texel.</summary>
    public static uint Pack((byte R, byte G, byte B) srgb) => (uint)(srgb.R | (srgb.G << 8) | (srgb.B << 16) | (255 << 24));

    private static int ToByte(float c) => (int)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
}
