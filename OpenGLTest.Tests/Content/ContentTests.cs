using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Graphics;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Content;

public sealed class ContentTests
{
    public ContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void BlockAndItemIdsFitInSharedByteRange()
    {
        var ids = BlockType.Types.Keys
            .Concat(ItemRegistry.All.Where(i => i is not BlockItem).Select(i => i.Id))
            .ToArray();
        Assert.NotEmpty(ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEqual(BlockType.Air, id));
        Assert.True(ids.Length <= 255);
    }

    [Fact]
    public void FlatMirrorsAgreeWithEveryBlockType()
    {
        // The mesher, the light flood and AO read only the flat per-id arrays, so every
        // renderer's forced flags must reach them, not just the BlockType record
        foreach (var (id, type) in BlockType.Types)
        {
            Assert.Equal(type.OccludesNeighbors, BlockType.OccludesById[id]);
            Assert.Equal(type.CastsAmbientOcclusion, BlockType.CastsAoById[id]);
            Assert.Equal(type.PassesLight, BlockType.PassesLightById[id]);
            Assert.Equal(type.Renderer == BlockRenderer.TreeWood, BlockType.IsTreeWood(id));
            if (type.Renderer != BlockRenderer.TreeWood) continue;

            // Tree wood is drawn down to a 1 px twig, which the per-id flags cannot see
            Assert.False(type.OccludesNeighbors);
            Assert.False(type.CastsAmbientOcclusion);
            Assert.True(type.PassesLight);
            Assert.False(BlockType.IsGreedy(id));
        }
    }

    [Fact]
    public void WaterIsForcedToFluidBehavior()
    {
        var water = ContentRegistry.RequireBlock("water");
        Assert.False(water.Solid);
        Assert.True(water.Fluid);
        Assert.True(water.PassesLight);
        Assert.False(water.HasItem);
        Assert.Same(LootTable.None, water.LootTable);
        Assert.True(BlockType.IsFluid(water.Id));
    }

    [Fact]
    public void FullCubeOccludesAfterBake()
    {
        Assert.True(ContentRegistry.RequireBlock("grass").OccludesNeighbors);
    }

    [Fact]
    public void AtlasIsOneSlicePerTextureInFileNameOrder()
    {
        var root = TestContent.FindAssetsRoot();
        var files = System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "textures", "blocks"), "*.png")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(files.Length, TextureAtlas.LayerCount);
        Assert.Equal(Array.IndexOf(files, "stone"), TextureAtlas.TileOf("stone"));
        Assert.Equal(TextureAtlas.TileOf("grass_top"), ContentRegistry.RequireBlock("grass").FaceTiles[4]);
        Assert.Equal(TextureAtlas.TileOf("grass_side"), ContentRegistry.RequireBlock("grass").FaceTiles[0]);
    }

    [Fact]
    public void BiomeTintLeavesTheModelTilesAlone()
    {
        // The tinted tiles live beside the model's: the default colored tiles stay the block's
        // icon, drop and held look, and the block still takes the greedy cube-face path.
        var grass = ContentRegistry.RequireBlock("grass");
        Assert.NotNull(grass.BiomeTint);
        Assert.Equal(TextureAtlas.TileOf("grass_top"), grass.FaceTiles[4]);
        Assert.Equal(TextureAtlas.TileOf("grass_side"), grass.FaceTiles[0]);
        Assert.True(BlockType.IsGreedy(grass.Id));
    }

    [Fact]
    public void BiomeTintResolvesReplacementsAndOverlays()
    {
        var grass = ContentRegistry.RequireBlock("grass");
        var bt = grass.BiomeTint!;
        Assert.Equal(BiomeTintPalette.Grass, bt.Palette);

        // "tile": the face is wholly replaced, so no base quad is left under it.
        Assert.Equal(BiomeTintFaces.NoTile, bt.Base[4]);
        Assert.Equal(TextureAtlas.TileOf("grass_top_textureless"), bt.Tinted[4]);

        // "side" with a base: every horizontal face draws the base, then the overlay.
        for (var f = 0; f < 4; f++)
        {
            Assert.True(bt.IsOverlay(f));
            Assert.Equal(TextureAtlas.TileOf("dirt"), bt.Base[f]);
            Assert.Equal(TextureAtlas.TileOf("grass_side_textureless"), bt.Tinted[f]);
        }

        // A face the file does not name is drawn exactly as the model says.
        Assert.False(bt.IsTinted(5));
        Assert.Equal(grass.FaceTiles[5], bt.Base[5]);
    }

    [Fact]
    public void AlphaTestedTintTilesGetCutoutMips()
    {
        var cutoutTiles = BlockType.CutoutTiles();
        var any = false;
        foreach (var block in BlockType.ById)
        {
            if (block?.BiomeTint is not { } bt) continue;
            for (var f = 0; f < 6; f++)
            {
                if (!bt.IsTinted(f) || !(bt.IsOverlay(f) || block.Cutout)) continue;
                any = true;
                Assert.Contains(bt.Tinted[f], cutoutTiles);
            }
        }
        Assert.True(any, "no block wears an alpha-tested biome-tint tile");
    }

    [Fact]
    public void BiomeTintRejectsMalformedFaces()
    {
        BlockDefinition.BiomeTintDefinition Def(string? palette, params (string Name, BlockDefinition.BiomeTintFaceDefinition Face)[] faces) =>
            new() { Palette = palette, Faces = faces.ToDictionary(f => f.Name, f => f.Face) };
        var tile = new BlockDefinition.BiomeTintFaceDefinition { Tile = "a" };

        void Rejects(BlockDefinition.BiomeTintDefinition dto)
        {
            var e = Assert.Throws<InvalidOperationException>(() => BiomeTintFaces.Expand(dto, "data/blocks/x.json"));
            Assert.Contains("data/blocks/x.json", e.Message);
        }

        Rejects(Def("purple", ("up", tile)));
        Rejects(Def(null));
        Rejects(Def(null, ("top", tile)));
        Rejects(Def(null, ("north", tile), ("side", tile)));
        Rejects(Def(null, ("up", new BlockDefinition.BiomeTintFaceDefinition())));
        Rejects(Def(null, ("up", new BlockDefinition.BiomeTintFaceDefinition { Tile = "a", Overlay = "b" })));
        Rejects(Def(null, ("up", new BlockDefinition.BiomeTintFaceDefinition { Tile = "a", Base = "b" })));

        // The shorthands expand onto the faces they name, and the palette name is case-blind.
        var (palette, expanded) = BiomeTintFaces.Expand(Def("Foliage", ("all", tile)), "data/blocks/x.json");
        Assert.Equal(BiomeTintPalette.Foliage, palette);
        Assert.All(expanded, f => Assert.Same(tile, f));
        var (_, sides) = BiomeTintFaces.Expand(Def(null, ("side", tile)), "data/blocks/x.json");
        Assert.All(sides.Take(4), f => Assert.Same(tile, f));
        Assert.Null(sides[4]);
        Assert.Null(sides[5]);
    }

    [Fact]
    public void BakedQuadsCarryTileLocalUvs()
    {
        // Every face of a full cube spans its whole tile: local UVs sit at 0 or 1 and the quad
        // names the slice it samples, so a merged quad can later run the same UV past 1.
        var stone = ContentRegistry.RequireBlock("stone").BakedModel!;
        Assert.Equal(6, stone.Quads.Length);
        foreach (var quad in stone.Quads)
        {
            Assert.Equal(TextureAtlas.TileOf("stone"), quad.Tile);
            foreach (var uv in quad.Uvs)
            {
                Assert.True(uv.X is 0f or 1f, $"u = {uv.X}");
                Assert.True(uv.Y is 0f or 1f, $"v = {uv.Y}");
            }
        }
    }

    [Fact]
    public void GlassIsTranslucentAndSelfCulling()
    {
        var glass = ContentRegistry.RequireBlock("glass");
        Assert.True(glass.Translucent);
        Assert.True(glass.CullSelf);
        Assert.False(glass.OccludesNeighbors);
        Assert.True(glass.PassesLight);
        Assert.False(glass.CastsAmbientOcclusion);
        Assert.False(BlockType.Occludes(glass.Id));
    }

    [Fact]
    public void StoneAlwaysDropsOneCobblestone()
    {
        var table = ContentRegistry.LootTableFor("stone");
        Assert.NotNull(table);
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var into = new List<byte>();
        for (var i = 0; i < 10; i++)
        {
            into.Clear();
            table.Roll(new LootContext(0, 0, 0), into);
            Assert.Equal(new[] { cobble }, into);
        }
    }

    [Fact]
    public void StoneBricksCraftFromStone()
    {
        var bricks = ContentRegistry.RequireBlock("stone_bricks");
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var recipe = ContentRegistry.RecipeForResult(bricks.Id);
        Assert.NotNull(recipe);
        Assert.Equal(4, recipe!.ResultCount);
        Assert.Contains(recipe.Ingredients, i => i.Accepts(stone) && i.Count == 4);
    }

    [Fact]
    public void GravelSometimesDropsFlint()
    {
        var gravel = ContentRegistry.RequireBlock("gravel");
        Assert.NotNull(gravel.LootTable);
        var flint = ContentRegistry.RequireItem("flint").Id;
        var into = new List<byte>();
        var flintRolls = 0;
        const int rolls = 4000;
        for (var i = 0; i < rolls; i++)
        {
            into.Clear();
            gravel.LootTable!.Roll(new LootContext(0, 0, 0), into);
            Assert.Single(into);
            if (into[0] == flint) flintRolls++;
            else Assert.Equal(gravel.Id, into[0]);
        }

        // The flint share lands in the 10-20 % band (a 1-in-6 weight)
        var share = flintRolls / (float)rolls;
        Assert.InRange(share, 0.08f, 0.25f);
    }

    [Fact]
    public void TntDropsItselfWhenDestroyed()
    {
        var tnt = ContentRegistry.RequireBlock("tnt");
        Assert.Null(tnt.LootTable);
        var into = new List<byte>();
        DropManager.RollLoot(tnt.Id, new LootContext(0, 0, 0), into);
        Assert.Equal(new[] { tnt.Id }, into);
    }

    [Fact]
    public void NoneLootTableDropsNothing()
    {
        var into = new List<byte>();
        LootTable.None.Roll(new LootContext(0, 0, 0), into);
        Assert.Empty(into);
    }

    [Fact]
    public void EveryBlockHasADisplayName()
    {
        Assert.All(BlockType.Types.Values,
            type => Assert.False(string.IsNullOrWhiteSpace(type.DisplayName)));
    }

    [Fact]
    public void UnknownContentThrowsNamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContentRegistry.RequireBlock("nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void DisplayMatrixAppliesYawThenPitchThenRollUnderScale()
    {
        // Whatever pose an item declares, the held transform must be its scale, then a yaw
        // about Y, a pitch about X and a roll about Z in that order, then translation
        // (which never turns the sprite). Angles come from the item, so this locks the
        // axis/order contract rather than any one pose.
        var item = ContentRegistry.RequireItem("compass");
        var radians = item.DisplayRotation * (MathF.PI / 180f);
        var rotation = Matrix4x4.CreateRotationY(radians.Y)
            * Matrix4x4.CreateRotationX(radians.X)
            * Matrix4x4.CreateRotationZ(radians.Z);

        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var actual = Vector3.TransformNormal(axis, item.DisplayMatrix());
            var expected = Vector3.TransformNormal(axis, rotation) * item.DisplayScale;
            Assert.Equal(expected.X, actual.X, 4);
            Assert.Equal(expected.Y, actual.Y, 4);
            Assert.Equal(expected.Z, actual.Z, 4);
        }
    }

    [Fact]
    public void RayIntersectsFlowerBoxButMissesItsEmptyRim()
    {
        var min = new Vector3(1.25f, 0f, 1.25f);
        var max = new Vector3(1.75f, 1f, 1.75f);
        var origin = new Vector3(1.5f, 2f, 1.5f);
        Assert.True(BlockRaycaster.RayIntersectsBox(origin, -Vector3.UnitY, min, max, 10f, out var t, out var normal));
        Assert.Equal(1f, t, precision: 4);
        Assert.Equal(Vector3.UnitY, normal);

        var rimOrigin = new Vector3(1.1f, 2f, 1.5f);
        Assert.False(BlockRaycaster.RayIntersectsBox(rimOrigin, -Vector3.UnitY, min, max, 10f, out _, out _));
    }

    private static Vector3? DeclaredSrgb(byte id) =>
        ContentRegistry.DeclaredLightColor(id) is { } c ? new Vector3(c[0], c[1], c[2]) / 255f : null;

    [Fact]
    public void EmittersDeclaringNoColorTakeTheDefaultSlot()
    {
        var emitters = BlockType.Types.Keys.Where(id => BlockType.Emission[id] > 0 && DeclaredSrgb(id) == null).ToArray();
        Assert.NotEmpty(emitters);
        Assert.All(emitters, id => Assert.Equal(0, BlockType.LightSlot[id]));
    }

    [Fact]
    public void DistinctDeclaredLightColorsGetDistinctSlots()
    {
        var declared = BlockType.Types.Keys.Where(id => DeclaredSrgb(id) != null).ToArray();
        Assert.NotEmpty(declared);
        foreach (var a in declared)
        foreach (var b in declared)
        {
            var d = Vector3.Abs(DeclaredSrgb(a)!.Value - DeclaredSrgb(b)!.Value);
            if (Math.Max(d.X, Math.Max(d.Y, d.Z)) > 0.5f / 255f)
                Assert.NotEqual(BlockType.LightSlot[a], BlockType.LightSlot[b]);
        }
    }

    [Fact]
    public void ANonDefaultDeclaredColorLightsWithItsOwnPaletteColor()
    {
        var nonDefault = BlockType.Types.Keys
            .Where(id => DeclaredSrgb(id) is { } c && Vector3.Distance(c, BlockLightPalette.DefaultSrgb) > 1f / 255f)
            .ToArray();
        Assert.NotEmpty(nonDefault);
        foreach (var id in nonDefault)
        {
            var slot = BlockType.LightSlot[id];
            Assert.NotEqual(BlockLightPalette.Linear[0], BlockLightPalette.Linear[slot]);
            var d = Vector3.Abs(DeclaredSrgb(id)!.Value - BlockLightPalette.SrgbOf(slot));
            Assert.True(Math.Max(d.X, Math.Max(d.Y, d.Z)) <= 0.5f / 255f + 1e-6f);
        }
    }
}
