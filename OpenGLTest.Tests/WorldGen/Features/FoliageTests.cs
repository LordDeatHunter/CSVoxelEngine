using System;
using System.Collections.Generic;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Graphics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Ground-cover foliage: the two cross plants resolve their climate palette through
/// <c>modelTint</c>, the mesher tints every one of their model quads (and leaves an untinted
/// cross block alone), the worldgen stamps each only on its own biome's ground, and each rolls
/// its own 0-2 loot.
/// </summary>
public sealed class FoliageTests
{
    public FoliageTests() => TestContent.EnsureLoaded();

    private static BlockType TallGrass => ContentRegistry.RequireBlock("tall_grass");
    private static BlockType DeadShrub => ContentRegistry.RequireBlock("dead_shrub");

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    private static bool Near(Vertex v, byte r, byte g, byte b)
    {
        var (vr, vg, vb) = Rgb(v);
        return Math.Abs(vr - r) <= 1 && Math.Abs(vg - g) <= 1 && Math.Abs(vb - b) <= 1;
    }

    [Fact]
    public void ModelTintResolvesOnlyOnSubCellModels()
    {
        foreach (var plant in new[] { TallGrass, DeadShrub })
        {
            Assert.Equal(BlockRenderer.Model, plant.Renderer);
            Assert.False(BlockType.IsGreedy(plant.Id), $"{plant.Name} must keep the per-quad model path");
            Assert.Equal(BiomeTintPalette.Grass, plant.ModelBiomeTint);
            Assert.Null(plant.BiomeTint);
        }

        // A full cube keeps the per-face biomeTint path and never a modelTint.
        var grass = ContentRegistry.RequireBlock("grass");
        Assert.Null(grass.ModelBiomeTint);
        Assert.NotNull(grass.BiomeTint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModelTintColorsEveryModelQuad(bool greedy)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, TallGrass.Id, 0);

        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.Vertices);

        var palette = TallGrass.ModelBiomeTint!.Value;
        var field = BiomeTint.FieldFor(palette, (int)center.Position.X, (int)center.Position.Z);
        var tile = TextureAtlas.TileOf("tall_grass_textureless");
        foreach (var v in mesh.Vertices)
        {
            Assert.True(v.IsBiomeTinted, $"plant vertex at {v.Position} is not flagged");
            Assert.Equal(tile, v.Layer);
            // The cross model is unshaded, so its vertex rgb is 255 * the field's tint.
            BiomeTint.Shade(255, field.Sample(v.Position.X, v.Position.Z), out var r, out var g, out var b);
            Assert.True(Near(v, r, g, b), $"vertex at {v.Position}: rgb {Rgb(v)} is not the field's tint");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUntintedCrossBlockStaysUncolored(bool greedy)
    {
        var flower = ContentRegistry.RequireBlock("flower");
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, flower.Id, 0);

        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.Vertices);
        Assert.DoesNotContain(mesh.Vertices, v => v.IsBiomeTinted);
    }

    [Fact]
    public void FoliageStampsOnlyOnItsOwnGround()
    {
        // A biome boundary can run through a chunk, so sum a few of each and gate every plant
        // cell rather than pinning one chunk's count.
        var grassFound = 0;
        var shrubFound = 0;
        foreach (var (cx, cz) in FindChunks((x, z) => TerrainGenerator.IsForest(x, z), 4))
            grassFound += StampMixedGround(cx, cz).Grass;
        foreach (var (cx, cz) in FindChunks((x, z) => TerrainGenerator.IsDesert(x, z), 8))
            shrubFound += StampMixedGround(cx, cz).Shrub;

        Assert.True(grassFound > 0, "expected tall grass across the sampled forest chunks");
        Assert.True(shrubFound > 0, "expected dead shrubs across the sampled desert chunks");
    }

    [Fact]
    public void LootIsOnlyTheNamedDrop()
    {
        var vine = ContentRegistry.RequireItem("vine").Id;
        var branch = ContentRegistry.RequireItem("branch").Id;

        var grassSupport = Support(into => TallGrass.LootTable!.Roll(new LootContext(3, 40, -7), into));
        Assert.Equal(new HashSet<byte> { vine }, grassSupport);

        var shrubSupport = Support(into => DeadShrub.LootTable!.Roll(new LootContext(3, 40, -7), into));
        Assert.Equal(new HashSet<byte> { branch }, shrubSupport);
    }

    private const int LootRolls = 500;

    private static HashSet<byte> Support(Action<List<byte>> roll)
    {
        var into = new List<byte>();
        var ids = new HashSet<byte>();
        for (var i = 0; i < LootRolls; i++)
        {
            into.Clear();
            roll(into);
            foreach (var id in into) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Stamps ground cover over a synthetic chunk whose ground alternates stone / grass / sand,
    /// and checks the gate both ways: every plant sits on its own ground and the biome agrees,
    /// nothing lands on stone, and the counts are returned so the caller can require the plants
    /// actually appeared (a zero-plant run must not pass vacuously).
    /// </summary>
    private static (int Grass, int Shrub) StampMixedGround(int chunkX, int chunkZ)
    {
        var tallGrass = TallGrass.Id;
        var deadShrub = DeadShrub.Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;

        const int groundLy = 8;
        var cells = Chunk.Size * Chunk.Size;
        var groundY = WorldGenerationConfig.SeaLevel + 20;
        var heights = new int[cells];
        var ground = new byte[cells];
        var blocks = new byte[Chunk.BlockCount];

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i = (x << Chunk.SizeShift) | z;
            heights[i] = groundY;
            ground[i] = ((x + z) % 3) switch
            {
                0 => stone,
                1 => grass,
                _ => sand,
            };
            blocks[Chunk.Idx(x, groundLy, z)] = ground[i];
        }

        var column = new ColumnData(heights, new byte[cells], new float[cells], new float[cells]);
        FoliageGenerator.Stamp(blocks, column, chunkX * Chunk.Size, groundY - groundLy, chunkZ * Chunk.Size);

        var grassFound = 0;
        var shrubFound = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var cell = blocks[Chunk.Idx(x, groundLy + 1, z)];
            if (cell == BlockType.Air) continue;

            var surface = ground[(x << Chunk.SizeShift) | z];
            var wx = chunkX * Chunk.Size + x;
            var wz = chunkZ * Chunk.Size + z;
            if (cell == tallGrass)
            {
                grassFound++;
                Assert.Equal(grass, surface);
                Assert.True(TerrainGenerator.IsForest(wx, wz));
            }
            else if (cell == deadShrub)
            {
                shrubFound++;
                Assert.Equal(sand, surface);
                Assert.True(TerrainGenerator.IsDesert(wx, wz));
            }
            else
            {
                Assert.Fail($"unexpected foliage block {cell} at ({wx}, {wz}) over {surface}");
            }
        }

        return (grassFound, shrubFound);
    }

    private static List<(int Cx, int Cz)> FindChunks(Func<int, int, bool> predicate, int wanted)
    {
        const int maxChunks = 320;
        const int step = 2;
        var found = new List<(int, int)>();
        for (var cx = -maxChunks; cx <= maxChunks && found.Count < wanted; cx += step)
        for (var cz = -maxChunks; cz <= maxChunks && found.Count < wanted; cz += step)
        {
            if (predicate(cx * Chunk.Size + 8, cz * Chunk.Size + 8)) found.Add((cx, cz));
        }

        Assert.True(found.Count >= wanted, $"found only {found.Count} matching chunks of {wanted}");
        return found;
    }
}
