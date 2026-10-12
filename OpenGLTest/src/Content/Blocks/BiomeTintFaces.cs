using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// A biome-tinted block's resolved faces (<see cref="BlockType.BiomeTint"/>): per face, the
/// untinted quad's tile and the tinted quad's tile, in <see cref="Chunk.CubeFaces"/> order
/// (-Z, +Z, -X, +X, +Y, -Y). A face carrying both is an overlay (the base drawn first, the tinted
/// cutout over it); a face carrying only the tinted tile is wholly replaced; a face carrying only
/// the base is drawn untinted, exactly as the model says.
/// </summary>
public sealed class BiomeTintFaces
{
    /// <summary>Marks a face that has no quad of that kind.</summary>
    public const ushort NoTile = ushort.MaxValue;

    /// <summary>Which climate palette tints the <see cref="Tinted"/> quads.</summary>
    public BiomeTintPalette Palette { get; }

    /// <summary>Six entries: the untinted quad's tile, <see cref="NoTile"/> when the face is
    /// wholly replaced by its tinted tile.</summary>
    public ushort[] Base { get; }

    /// <summary>Six entries: the tinted quad's tile, <see cref="NoTile"/> on an untinted face.</summary>
    public ushort[] Tinted { get; }

    public BiomeTintFaces(BiomeTintPalette palette, ushort[] baseTiles, ushort[] tintedTiles)
    {
        if (baseTiles.Length != 6)
            throw new ArgumentException("A biome tint needs exactly six base tiles.", nameof(baseTiles));
        if (tintedTiles.Length != 6)
            throw new ArgumentException("A biome tint needs exactly six tinted tiles.", nameof(tintedTiles));
        Palette = palette;
        Base = baseTiles;
        Tinted = tintedTiles;
    }

    /// <summary>Whether the face draws an untinted base with a tinted overlay on top.</summary>
    public bool IsOverlay(int face) => Base[face] != NoTile && Tinted[face] != NoTile;

    /// <summary>Whether the face draws a tinted quad at all.</summary>
    public bool IsTinted(int face) => Tinted[face] != NoTile;

    /// <summary>Whether any face is an overlay.</summary>
    public bool HasOverlay
    {
        get
        {
            for (var f = 0; f < 6; f++)
                if (IsOverlay(f)) return true;
            return false;
        }
    }

    // Minecraft face names in Chunk.CubeFaces order, for error messages.
    private static readonly string[] SingleFaceNames = ["north", "south", "west", "east", "up", "down"];

    private static readonly Dictionary<string, int[]> FaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = [0],
        ["south"] = [1],
        ["west"] = [2],
        ["east"] = [3],
        ["up"] = [4],
        ["down"] = [5],
        ["side"] = [0, 1, 2, 3],
        ["all"] = [0, 1, 2, 3, 4, 5],
    };

    /// <summary>
    /// Validates a block file's <c>biomeTint</c> object and spreads it over the six faces (null
    /// where the face is untinted). Needs no atlas, so the load loop runs it to fail early with
    /// the file name; the tile names are resolved later, in <c>ContentRegistry.BakeModels</c>.
    /// </summary>
    internal static (BiomeTintPalette Palette, BlockDefinition.BiomeTintFaceDefinition?[] Faces) Expand(
        BlockDefinition.BiomeTintDefinition dto, string path)
    {
        var palette = ParsePalette(dto.Palette, path);

        if (dto.Faces == null || dto.Faces.Count == 0)
            throw new InvalidOperationException(
                $"Block '{path}' declares a biomeTint with no faces; name at least one face under 'faces'.");

        var faces = new BlockDefinition.BiomeTintFaceDefinition?[6];
        foreach (var (name, entry) in dto.Faces)
        {
            if (!FaceNames.TryGetValue(name, out var indices))
                throw new InvalidOperationException(
                    $"Block '{path}' names biomeTint face '{name}'; valid faces are {string.Join(", ", FaceNames.Keys.Select(k => $"'{k}'"))}.");
            if (entry == null)
                throw new InvalidOperationException(
                    $"Block '{path}' gives biomeTint face '{name}' no value; set 'tile' or 'overlay'.");

            var hasTile = entry.Tile != null;
            var hasOverlay = entry.Overlay != null;
            if (hasTile == hasOverlay)
                throw new InvalidOperationException(
                    $"Block '{path}' biomeTint face '{name}' must set exactly one of 'tile' and 'overlay'.");
            if (entry.Base != null && !hasOverlay)
                throw new InvalidOperationException(
                    $"Block '{path}' biomeTint face '{name}' sets 'base' without 'overlay'; a base only sits under an overlay.");

            foreach (var index in indices)
            {
                if (faces[index] != null)
                    throw new InvalidOperationException(
                        $"Block '{path}' names biomeTint face '{SingleFaceNames[index]}' more than once (shorthands 'side' and 'all' included).");
                faces[index] = entry;
            }
        }

        return (palette, faces);
    }

    /// <summary>Parses a palette name, case-blind: null or <c>"grass"</c> is the default,
    /// <c>"foliage"</c> the other; anything else throws naming the block's file.</summary>
    internal static BiomeTintPalette ParsePalette(string? name, string path)
    {
        if (name == null || string.Equals(name, "grass", StringComparison.OrdinalIgnoreCase))
            return BiomeTintPalette.Grass;
        if (string.Equals(name, "foliage", StringComparison.OrdinalIgnoreCase))
            return BiomeTintPalette.Foliage;
        throw new InvalidOperationException(
            $"Block '{path}' names biome tint palette '{name}'; valid palettes are 'grass' and 'foliage'.");
    }
}
