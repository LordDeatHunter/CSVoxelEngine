using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Graphics;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Effects.Climate;

/// <summary>
/// The mining pop and the break debris of a biome-tinted block: they wear its world tiles
/// (a base quad, then a tinted quad flagged by Vertex bit 4) with the rgb left at the shade,
/// since the tint itself is the plain path's per-draw uniform, and the debris carries the
/// field's tint at the cell it chipped off. An untinted block is unchanged.
/// </summary>
public sealed class BiomeTintPopTests
{
    public BiomeTintPopTests() => TestContent.EnsureLoaded();

    private static BlockType Grass => ContentRegistry.RequireBlock("grass");
    private static BlockType Leaves => ContentRegistry.RequireBlock("oak_leaves");
    private static BlockType Stone => ContentRegistry.RequireBlock("stone");

    private static BiomeTintFaces TintOf(BlockType type) =>
        type.BiomeTint ?? throw new InvalidOperationException($"{type.Name} declares no biomeTint");

    private static (List<Vertex> Verts, List<ushort> Indices) Emit(BlockType type, BiomeTintFaces? tint)
    {
        var verts = new List<Vertex>();
        var indices = new List<ushort>();
        ushort offset = 0;
        PopBoxEmitter.EmitBox(verts, indices, ref offset, BlockHitbox.FullCube, BlockHitbox.FullCube,
            Vector3.Zero, type.FaceTiles, tint);
        return (verts, indices);
    }

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    /// <summary>The quads in index order, each as its first index's vertex quad (4k..4k+3).</summary>
    private static List<Vertex[]> Quads(IReadOnlyList<Vertex> verts, IReadOnlyList<ushort> indices)
    {
        var quads = new List<Vertex[]>();
        for (var i = 0; i < indices.Count; i += 6)
        {
            var first = indices[i];
            quads.Add([verts[first], verts[first + 1], verts[first + 2], verts[first + 3]]);
        }
        return quads;
    }

    private static bool SamePositions(Vertex[] a, Vertex[] b) =>
        a.Select(v => v.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)
            .SequenceEqual(b.Select(v => v.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z));

    /// <summary>
    /// The tinted-tile rules shared by the pop and the debris cube: a vertex is flagged exactly
    /// when it wears a tinted tile, the rgb is gray (shade only), and every overlay face draws
    /// its base quad before a coplanar tinted quad.
    /// </summary>
    private static void AssertTintedLayout(BiomeTintFaces bt, IReadOnlyList<Vertex> verts, IReadOnlyList<ushort> indices)
    {
        Assert.NotEmpty(verts);
        foreach (var v in verts)
        {
            var face = v.Normal & 7;
            Assert.Equal(v.Layer == bt.Tinted[face], v.IsBiomeTinted);
            var (r, g, b) = Rgb(v);
            Assert.True(r == g && g == b, "the pop's rgb is the shade alone; the tint is the uniform");
        }

        var quads = Quads(verts, indices);
        for (var face = 0; face < 6; face++)
        {
            if (!bt.IsOverlay(face)) continue;
            var tinted = quads.FindIndex(q => (q[0].Normal & 7) == face && q[0].Layer == bt.Tinted[face]);
            Assert.True(tinted >= 0, $"face {face} has no tinted quad");
            var basePrior = quads.Take(tinted).Any(q => (q[0].Normal & 7) == face && q[0].Layer == bt.Base[face]
                                                        && SamePositions(q, quads[tinted]));
            Assert.True(basePrior, $"face {face}'s tinted quad is not drawn over an earlier coplanar base");
        }
    }

    [Fact]
    public void PopOfATintedBlockFlagsOnlyItsTintedQuads()
    {
        var bt = TintOf(Grass);
        Assert.True(bt.HasOverlay, "grass's sides are an overlay over a base");
        var (verts, indices) = Emit(Grass, bt);
        AssertTintedLayout(bt, verts, indices);
        Assert.Contains(verts, v => v.IsBiomeTinted);
    }

    [Fact]
    public void PopOfAnUntintedBlockIsUnchanged()
    {
        Assert.Null(Stone.BiomeTint);
        var (withParam, withParamIdx) = Emit(Stone, Stone.BiomeTint);

        var plain = new List<Vertex>();
        var plainIdx = new List<ushort>();
        ushort offset = 0;
        PopBoxEmitter.EmitBox(plain, plainIdx, ref offset, BlockHitbox.FullCube, BlockHitbox.FullCube,
            Vector3.Zero, Stone.FaceTiles);

        Assert.Equal(plain, withParam);
        Assert.Equal(plainIdx, withParamIdx);
        Assert.DoesNotContain(withParam, v => v.IsBiomeTinted);
        foreach (var v in withParam)
            Assert.Equal(Stone.FaceTiles[v.Normal & 7], v.Layer);
    }

    [Fact]
    public void EveryTintedBlocksPopFitsTheQuadBudget()
    {
        var any = false;
        foreach (var type in BlockType.ById)
        {
            if (type?.BiomeTint is not { } bt) continue;
            any = true;
            foreach (var box in type.Hitboxes)
            {
                var verts = new List<Vertex>();
                var indices = new List<ushort>();
                ushort offset = 0;
                PopBoxEmitter.EmitBox(verts, indices, ref offset, box, box, Vector3.Zero, type.FaceTiles, bt);
                Assert.True(indices.Count / 6 <= PopBoxEmitter.MaxQuadsPerBox,
                    $"{type.Name}'s pop box emits {indices.Count / 6} quads, over the streaming budget");
            }
        }
        Assert.True(any, "no block declares a biomeTint");
    }

    [Theory]
    [InlineData("grass")]
    [InlineData("oak_leaves")]
    public void DebrisCarriesTheCellTintOfATintedBlockOnly(string blockName)
    {
        var type = ContentRegistry.RequireBlock(blockName);
        var bt = TintOf(type);
        var cell = new Vector3(-37f, 12f, -5f);
        var system = new BlockDebrisSystem();
        system.Burst(cell, type.Id, WorldGenerationConfig.BlockDebrisPerHit, new Random(1));
        Assert.NotEmpty(system.Debris);
        var expected = BiomeTint.CellTint(bt.Palette, (int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Z));
        foreach (var debris in system.Debris)
            Assert.Equal(expected, debris.Tint);

        var stone = new BlockDebrisSystem();
        stone.Burst(cell, Stone.Id, WorldGenerationConfig.BlockDebrisPerHit, new Random(1));
        Assert.NotEmpty(stone.Debris);
        foreach (var debris in stone.Debris)
            Assert.Equal(Vector3.One, debris.Tint);
    }

    /// <summary>
    /// A column source for the debris tint: answers from <see cref="Answer"/> (null = the column is
    /// missing, as a client's streamed column before it arrived) and records every column asked.
    /// </summary>
    private sealed class FakeColumnSource(Func<int, int, ColumnTerrain?> answer) : IColumnSource
    {
        public readonly HashSet<(int X, int Z)> Asked = new();
        private Func<int, int, ColumnTerrain?> Answer { get; } = answer;

        public bool IsReady(int chunkX, int chunkZ) => Answer(chunkX, chunkZ) != null;

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
        {
            Asked.Add((chunkX, chunkZ));
            var got = Answer(chunkX, chunkZ);
            terrain = got ?? default;
            return got != null;
        }
    }

    [Theory]
    [InlineData("grass")]
    [InlineData("oak_leaves")]
    public void DebrisFallsBackToUntintedWhenTheColumnIsMissing(string blockName)
    {
        var type = ContentRegistry.RequireBlock(blockName);
        var bt = TintOf(type);
        var cell = new Vector3(-37f, 12f, -5f);
        var x = (int)MathF.Floor(cell.X);
        var z = (int)MathF.Floor(cell.Z);
        var cx = x >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        var lattice = new HashSet<(int, int)> { (cx, cz), (cx + 1, cz), (cx, cz + 1), (cx + 1, cz + 1) };

        // A column the source does not have yet leaves every chip untinted, never a guess
        var missing = new FakeColumnSource((_, _) => null);
        var untinted = new BlockDebrisSystem();
        untinted.Burst(cell, type.Id, WorldGenerationConfig.BlockDebrisPerHit, new Random(1), columns: missing);
        Assert.NotEmpty(untinted.Debris);
        Assert.All(untinted.Debris, d => Assert.Equal(Vector3.One, d.Tint));
        Assert.NotEmpty(missing.Asked);

        // The real terrain, served through the source, gives exactly the worldgen cell tint, read
        // from the cell's four lattice columns and nothing else
        var real = new FakeColumnSource((qx, qz) =>
            WorldgenColumnSource.Instance.TryGet(qx, qz, out var t) ? t : null);
        var tinted = new BlockDebrisSystem();
        tinted.Burst(cell, type.Id, WorldGenerationConfig.BlockDebrisPerHit, new Random(1), columns: real);
        Assert.NotEmpty(tinted.Debris);
        var expected = BiomeTint.CellTint(bt.Palette, x, z);
        Assert.All(tinted.Debris, d => Assert.Equal(expected, d.Tint));
        Assert.True(lattice.SetEquals(real.Asked), "the debris read columns other than the cell's lattice corners");

        // And it is the passed source that decides, not worldgen behind it: a climate opposite to
        // the real one at every corner tints the chips with that climate's palette color
        Assert.True(WorldgenColumnSource.Instance.TryGet(cx, cz, out var here));
        var warmth = here.Warmth < 0.5f ? 1f : 0f;
        var wetness = here.Wetness < 0.5f ? 1f : 0f;
        var opposite = new FakeColumnSource((_, _) => new ColumnTerrain(
            new int[Chunk.Size * Chunk.Size], new int[Chunk.Size * Chunk.Size], warmth, wetness));
        var swapped = new BlockDebrisSystem();
        swapped.Burst(cell, type.Id, WorldGenerationConfig.BlockDebrisPerHit, new Random(1), columns: opposite);
        BiomeTint.TintsFromClimate(warmth, wetness, out var grass, out var foliage);
        var uniform = bt.Palette == BiomeTintPalette.Foliage ? foliage : grass;
        Assert.NotEmpty(swapped.Debris);
        foreach (var debris in swapped.Debris)
        {
            Assert.True(Vector3.Distance(uniform, debris.Tint) < 1e-5f,
                $"a uniform climate's field is that climate's tint everywhere, got {debris.Tint} for {uniform}");
            Assert.NotEqual(expected, debris.Tint);
        }
    }

    [Fact]
    public void TintedCubeMeshIsCenteredAndFlagsOnlyTintedQuads()
    {
        var bt = TintOf(Grass);
        var (verts, indices) = BlockCubeCache.BuildTintedCube(Grass);
        foreach (var v in verts)
        {
            Assert.InRange(v.Position.X, -0.5f, 0.5f);
            Assert.InRange(v.Position.Y, -0.5f, 0.5f);
            Assert.InRange(v.Position.Z, -0.5f, 0.5f);
        }
        AssertTintedLayout(bt, verts, indices);

        // The tile form replaces the face outright: a leaves cube is tinted on every quad
        var leavesBt = TintOf(Leaves);
        var (leafVerts, leafIndices) = BlockCubeCache.BuildTintedCube(Leaves);
        Assert.NotEmpty(leafVerts);
        Assert.All(leafVerts, v => Assert.True(v.IsBiomeTinted));
        AssertTintedLayout(leavesBt, leafVerts, leafIndices);
    }
}
