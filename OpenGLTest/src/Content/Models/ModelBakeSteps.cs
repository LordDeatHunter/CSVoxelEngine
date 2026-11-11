using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Graphics;

namespace VoxelEngine.Content.Models;

/// <summary>The steps <see cref="ContentRegistry.BakeModels"/> runs around its bake loop: the facing and multiblock bake checks, the open-model bake, the greedy-cube test, the biome and model tint resolution, and the built-in parent self-check.</summary>
internal static class ModelBakeSteps
{
    // Texture variables the built-in parents leave to their children; the startup self-check
    // fills them all with one real texture so the parents can be baked in isolation.
    private static readonly string[] ParentTextureVariables =
        ["all", "end", "side", "top", "bottom", "north", "south", "west", "east", "up", "down"];

    /// <summary>
    /// Turns each validated <c>biomeTint</c> into the block's <see cref="BiomeTintFaces"/>. The
    /// tint rides the plain cube-face emitters only, so the block must bake greedy and be opaque
    /// or cutout (never translucent), and an overlay needs an opaque base quad beneath it, so it
    /// is refused on a cutout block. The model's <see cref="BlockType.FaceTiles"/> are left alone:
    /// they stay the icon, drop and held look.
    /// </summary>
    internal static void ResolveBiomeTints()
    {
        foreach (var (id, palette, faces, path) in ContentRegistry.BiomeTintPending)
        {
            var type = BlockType.ById[id]!;
            if (!BlockType.GreedyById[id] || type.Translucent)
                throw new InvalidOperationException(
                    $"Block '{path}' declares biomeTint but its model is not a plain full cube; the tint rides the cube-face emitters only.");

            var baseTiles = (ushort[])type.FaceTiles.Clone();
            var tintedTiles = new ushort[6];
            Array.Fill(tintedTiles, BiomeTintFaces.NoTile);
            for (var f = 0; f < 6; f++)
            {
                if (faces[f] is not { } face) continue;
                if (face.Tile != null)
                {
                    baseTiles[f] = BiomeTintFaces.NoTile;
                    tintedTiles[f] = BiomeTintTileOf(face.Tile, path);
                    continue;
                }

                if (type.Cutout)
                    throw new InvalidOperationException(
                        $"Block '{path}' declares a biomeTint overlay on a cutout block; an overlay needs an opaque base quad.");
                tintedTiles[f] = BiomeTintTileOf(face.Overlay!, path);
                if (face.Base != null)
                    baseTiles[f] = BiomeTintTileOf(face.Base, path);
            }

            type.BiomeTint = new BiomeTintFaces(palette, baseTiles, tintedTiles);
        }
    }

    private static ushort BiomeTintTileOf(string tile, string path)
    {
        try
        {
            return TextureAtlas.TileOf(tile);
        }
        catch (KeyNotFoundException e)
        {
            throw new InvalidOperationException(
                $"Block '{path}' biomeTint names texture '{tile}', which is not an atlas tile.", e);
        }
    }

    /// <summary>
    /// Resolves each <c>modelTint</c> (a sub-cell model block tinted by the climate on every
    /// quad) into <see cref="BlockType.ModelBiomeTint"/>. Only a non-full-cube <c>model</c>
    /// renderer qualifies: a full cube belongs to <see cref="BiomeTint"/>'s per-face path, and a
    /// procedural renderer has no model quads. The check needs the greedy flag settled by the
    /// bake loop, so it runs beside <see cref="ResolveBiomeTints"/>.
    /// </summary>
    internal static void ResolveModelTints()
    {
        foreach (var (id, paletteName, path) in ContentRegistry.ModelTintPending)
        {
            var type = BlockType.ById[id]!;
            if (type.Renderer != BlockRenderer.Model || type.Translucent
                || BlockType.GreedyById[id] || type.BakedModel is not { IsFullCube: false })
                throw new InvalidOperationException(
                    $"Block '{path}' declares modelTint but its model is not a plain non-full-cube model; a full cube uses biomeTint's per-face tiles instead.");

            type.ModelBiomeTint = BiomeTintFaces.ParsePalette(paletteName, path);
        }
    }

    internal static BakedModel BakeOpenModel(BlockType type, BlockModel model)
    {
        try
        {
            return ModelBaker.Bake(model);
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to bake the open model of block '{type.Name}' ('{model.Source}'): {e.Message}", e);
        }
    }

    /// <summary>
    /// Checks that a facing block's model turns cleanly: no quad may cover a whole cell face,
    /// because a turned quad shades flat (see <see cref="HorizontalFacing.RotateModel"/>) while
    /// the authored north bake would take per-corner AO, so one facing would shade unlike the rest.
    /// </summary>
    internal static void ValidateFacingBake(BlockType type, BakedModel baked)
    {
        if (baked.Quads.Any(quad => quad.IsFullFace))
        {
            throw new InvalidOperationException(
                $"The model of facing block '{type.Name}' has a face covering a whole cell face; a turned model shades flat on every facing, so no face may span a full cell side.");
        }
    }

    /// <summary>
    /// Checks that a multiblock anchor's model can actually be drawn as a structure: it must stay
    /// inside its volume (0..16 * <see cref="BlockType.MultiblockSize"/> model units), cull against nothing (a cullface tests the anchor's world
    /// neighbor, which is the structure's own part cell) and carry no full-cell face (the
    /// per-corner AO path indexes its neighbor deltas by <see cref="Chunk.CubeFaces"/> corner
    /// order, which rotation breaks, so a multiblock shades flat on every facing).
    /// </summary>
    internal static void ValidateMultiblockBake(BlockType type, BakedModel baked)
    {
        const float slack = 1e-4f;
        var size = type.MultiblockSize;
        foreach (var quad in baked.Quads)
        {
            if (quad.CullFace >= 0)
                throw new InvalidOperationException(
                    $"The model of multiblock '{type.Name}' has a face with a cullface; a multiblock's faces are culled against nothing, since the anchor's neighbors are its own part cells.");
            if (quad.IsFullFace)
                throw new InvalidOperationException(
                    $"The model of multiblock '{type.Name}' has a face covering a whole cell face; a multiblock must shade flat on every facing, so no face may sit on a cell plane spanning the full cell.");
            foreach (var position in quad.Positions)
                if (position.X < -slack || position.X > size + slack ||
                    position.Y < -slack || position.Y > size + slack ||
                    position.Z < -slack || position.Z > size + slack)
                    throw new InvalidOperationException(
                        $"The model of multiblock '{type.Name}' has a corner at {position}, outside its {size}x{size}x{size} volume (0..{size * 16} in model units).");
        }
    }

    /// <summary>
    /// Whether a baked model is exactly the six untransformed cube faces, so the greedy mesher
    /// can rebuild any of its faces from (face, extent) alone: one full, shaded quad per cube
    /// face, culled against its own side, positioned on the <see cref="Chunk.CubeFaces"/>
    /// corners and textured with the plain <see cref="ChunkFaceTables.TiledUv"/> orientation. A rotated
    /// or cropped face UV, a sub-cell element or an unshaded face keeps the per-quad path.
    /// Cutout (leaves) and translucent (glass) cubes qualify: their culling rule is the same
    /// one, and the mesher picks the destination list per block type.
    /// </summary>
    internal static bool BakesGreedy(BakedModel baked)
    {
        if (!baked.IsFullCube || baked.Quads.Length != 6) return false;

        var seen = 0;
        foreach (var quad in baked.Quads)
        {
            if (!quad.IsFullFace || !quad.Shade || quad.CullFace != quad.Face) return false;
            if ((seen & (1 << quad.Face)) != 0) return false;
            seen |= 1 << quad.Face;
            if (quad.Tile != baked.FaceTiles[quad.Face]) return false;

            var corners = Chunk.CubeFaces[quad.Face].face;
            for (var j = 0; j < 4; j++)
            {
                if (quad.Positions[j] != corners[j]) return false;
                if (quad.Uvs[j] != ChunkFaceTables.TiledUv(quad.Face, corners[j], Vector3.One)) return false;
            }
        }

        return seen == 0x3F;
    }

    // Bakes the four built-in parents against a real texture: a plain cube must come out as six
    // full faces that cull against their own side, which is what every data-driven block and
    // every BlockBench export inherits.
    internal static void SelfCheckBuiltInParents()
    {
        var texturesFolder = Path.Combine(ContentRegistry.AssetsRoot, "textures", "blocks");
        var probe = Directory.GetFiles(texturesFolder, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
        if (probe == null) return;

        foreach (var parent in new[] { "block/cube", "block/cube_all", "block/cube_column", "block/cube_bottom_top" })
        {
            var resolved = ContentRegistry.ModelFiles!.Resolve(parent);
            var textures = new Dictionary<string, string>(resolved.Textures, StringComparer.Ordinal);
            foreach (var variable in ParentTextureVariables)
                textures.TryAdd(variable, probe);

            var baked = ModelBaker.Bake(new BlockModel(parent, textures, resolved.Elements));
            if (!baked.IsFullCube || baked.Quads.Length != 6)
                throw new InvalidOperationException(
                    $"Built-in model '{parent}' baked to {baked.Quads.Length} quads and IsFullCube={baked.IsFullCube}; expected 6 full faces.");

            foreach (var quad in baked.Quads)
                if (!quad.IsFullFace || quad.CullFace != quad.Face)
                    throw new InvalidOperationException(
                        $"Built-in model '{parent}' baked a quad on face {quad.Face} with cullface {quad.CullFace} (full face: {quad.IsFullFace}).");

            // Every full-cube block derives from one of these, so the greedy mesher must be
            // able to take all of them or most of the world falls back to the per-quad path
            if (!BakesGreedy(baked))
                throw new InvalidOperationException(
                    $"Built-in model '{parent}' does not bake to plain cube faces the greedy mesher can merge.");
        }
    }
}
