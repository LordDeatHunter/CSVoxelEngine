using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The re-seedable worldgen: <see cref="WorldSeed.Apply"/> rebuilds every static noise from the new
/// seed, clears the worldgen caches and the LOD edit state, a swap and a swap back reproduce the
/// original terrain, and a live world refuses the swap.
/// </summary>
[Collection("Worldgen seed")]
public class WorldSeedTests
{
    public WorldSeedTests() => TestContent.EnsureLoaded();

    private static readonly FieldInfo NoiseSeedField =
        typeof(FastNoiseLite).GetField("mSeed", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FastNoiseLite no longer has an mSeed field");

    private static List<FieldInfo> StaticNoiseFields() =>
        typeof(WorldSeed).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
                                         | BindingFlags.DeclaredOnly))
            .Where(f => f.FieldType == typeof(FastNoiseLite))
            .ToList();

    private static int SeedOf(FieldInfo field) => (int)NoiseSeedField.GetValue(field.GetValue(null))!;

    [Fact]
    public void EveryStaticNoiseFollowsTheSeed()
    {
        var fields = StaticNoiseFields();
        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.False(f.IsInitOnly, $"{f.DeclaringType!.Name}.{f.Name} is readonly"));

        try
        {
            var before = fields.ToDictionary(f => f, SeedOf);
            WorldSeed.Apply(WorldSeed.Default + 101);
            foreach (var field in fields)
                Assert.True(unchecked(SeedOf(field) - before[field]) == 101,
                    $"{field.DeclaringType!.Name}.{field.Name} did not follow the seed");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void ApplyChangesUncachedTerrainAndApplyBackRestoresIt()
    {
        try
        {
            var a = TerrainGenerator.Compute(3, -2);
            WorldSeed.Apply(WorldSeed.Default ^ 0x5A5A);
            var b = TerrainGenerator.Compute(3, -2);
            WorldSeed.Apply(WorldSeed.Default);
            var c = TerrainGenerator.Compute(3, -2);

            Assert.False(b.Heights.SequenceEqual(a.Heights), "a different seed must change the terrain");
            Assert.True(c.Heights.SequenceEqual(a.Heights));
            Assert.True(c.Bed.SequenceEqual(a.Bed));
            Assert.True(c.WaterLevel.SequenceEqual(a.WaterLevel));
            Assert.True(c.DirtDepth.SequenceEqual(a.DirtDepth));
            Assert.True(c.Biomes.SequenceEqual(a.Biomes));
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void ApplyIsRefusedWhileAWorldIsLive()
    {
        WorldSeed.BeginWorld();
        try
        {
            Assert.Throws<InvalidOperationException>(() => WorldSeed.Apply(WorldSeed.Default + 1));
            Assert.Equal(WorldSeed.Default, WorldSeed.Value);
        }
        finally
        {
            WorldSeed.EndWorld();
        }
    }

    [Fact]
    public void ApplyRoundTripReproducesCachedColumnData()
    {
        (int X, int Z)[] columns = [(0, 0), (-7, 3), (40, -40)];
        try
        {
            var a = columns.Select(c => ColumnCache.Get(c.X, c.Z)).ToArray();
            var surface = new Vector3(0, SurfaceChunkY(), 0);
            var blocksBefore = BlocksOf(new Chunk(null!, surface, generateBlockData: true));

            WorldSeed.Apply(WorldSeed.Default + 7);
            var b = columns.Select(c => ColumnCache.Get(c.X, c.Z)).ToArray();
            for (var i = 0; i < columns.Length; i++)
                Assert.False(ReferenceEquals(a[i], b[i]), $"column {columns[i]} was served from the old seed's cache");
            Assert.Contains(Enumerable.Range(0, columns.Length), i => !b[i].Heights.SequenceEqual(a[i].Heights));

            WorldSeed.Apply(WorldSeed.Default);
            var c = columns.Select(col => ColumnCache.Get(col.X, col.Z)).ToArray();
            for (var i = 0; i < columns.Length; i++)
            {
                Assert.False(ReferenceEquals(a[i], c[i]), $"column {columns[i]} survived two cache clears");
                Assert.True(c[i].Heights.SequenceEqual(a[i].Heights));
                Assert.True(c[i].Bed.SequenceEqual(a[i].Bed));
                Assert.True(c[i].WaterLevel.SequenceEqual(a[i].WaterLevel));
                Assert.True(c[i].DirtDepth.SequenceEqual(a[i].DirtDepth));
                Assert.True(c[i].Biomes.SequenceEqual(a[i].Biomes));
            }

            var blocksAfter = BlocksOf(new Chunk(null!, surface, generateBlockData: true));
            Assert.True(blocksAfter.SequenceEqual(blocksBefore), "the surface chunk did not regenerate identically");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void ApplyResetsEditState()
    {
        try
        {
            // (1) A column cap far from every other test's columns.
            var spec = LodRegion.Levels[1];
            var fx = 3300 * spec.RegionBlocks + 5;
            var fz = 3300 * spec.RegionBlocks + 9;
            var local = ((fx & Chunk.SizeMask) << Chunk.SizeShift) | (fz & Chunk.SizeMask);
            var y = ColumnCache.GetHeights(fx >> Chunk.SizeShift, fz >> Chunk.SizeShift)[local] - 40;
            Assert.True(LodRegion.LowerColumnBelow(fx, y, fz));
            Assert.False(LodRegion.LowerColumnBelow(fx, y, fz)); // the equal cap is refused while it stands

            // (2) A felled-canopy marker on the nearest canopy column.
            var (cx, cz) = FindCanopyColumn();
            TreeGenerator.PoisonCanopy(cx, cz, TreeGenerator.SingleEditPoisonRadius);
            Assert.False(TreeGenerator.TryGetCanopy(cx, cz, out _, out _));

            // (3) A surface override on another column of the same far region.
            var (x3, z3) = (fx + 20, fz + 20);
            LodSurface.Set(x3, z3, y + 60, TerrainGenerator.RockBlock);
            Assert.True(LodSurface.TryGet(x3, z3, out _, out _));

            // (4) The swap forgets all three.
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(LodRegion.LowerColumnBelow(fx, y, fz), "the cap outlived the world swap");
            Assert.True(TreeGenerator.TryGetCanopy(cx, cz, out _, out _), "the canopy marker outlived the world swap");
            Assert.False(LodSurface.TryGet(x3, z3, out _, out _), "the surface override outlived the world swap");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    private static int SurfaceChunkY()
    {
        var max = 0;
        foreach (var h in ColumnCache.GetHeights(0, 0))
            if (h > max) max = h;
        return max >> Chunk.SizeShift;
    }

    private static byte[] BlocksOf(Chunk chunk)
    {
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        var i = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[i++] = chunk.GetLocalBlock(x, y, z);
        return blocks;
    }

    // Walks square rings outward from the origin to the first column under a canopy.
    private static (int X, int Z) FindCanopyColumn()
    {
        const int MaxRadius = 512;
        for (var r = 0; r <= MaxRadius; r++)
        for (var x = -r; x <= r; x++)
        {
            // Only the ring's perimeter: the full rows at z = +-r, the two edge cells elsewhere.
            var edge = Math.Abs(x) == r;
            for (var z = -r; z <= r; z += edge || r == 0 ? 1 : 2 * r)
                if (TreeGenerator.TryGetCanopy(x, z, out _, out _)) return (x, z);
        }

        Assert.Fail($"no canopy column within {MaxRadius} blocks of the origin");
        return default;
    }
}
