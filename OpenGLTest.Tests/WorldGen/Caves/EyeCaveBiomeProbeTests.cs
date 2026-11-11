using System.Numerics;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>
/// The eye's cave biome probe (<see cref="EyeCaveBiomeProbe"/>, CB-3): it answers exactly what the point
/// query gives for the eye's cell over the source's stored column, reads no column at or below the barrier,
/// reads a column only when the eye changes cell, and is plain above the surface. In the "Worldgen seed"
/// collection because it reseeds.
/// </summary>
[Collection("Worldgen seed")]
public sealed class EyeCaveBiomeProbeTests
{
    private const int Cx = 0, Cz = 0, Lx = 8, Lz = 8;

    public EyeCaveBiomeProbeTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    [Fact]
    public void TheProbeEqualsThePointQueryDownTheSubBand()
    {
        var column = ColumnCache.Get(Cx, Cz);
        var i = (Lx << Chunk.SizeShift) | Lz;
        var x = Cx * Chunk.Size + Lx;
        var z = Cz * Chunk.Size + Lz;
        var anyTinted = false;
        var probe = new EyeCaveBiomeProbe();
        for (var y = CaveBiomes.SubBandTopY; y > WorldGenerationConfig.BarrierTopY; y--)
        {
            var expected = CaveBiomes.At(x, y, z, column.Biomes[i], column.Heights[i]);
            Assert.Equal(expected, probe.Update(GeneratedWorldSource.Instance, Eye(x, y, z)));
            anyTinted |= expected != CaveBiome.Plain;
        }
        Assert.True(anyTinted, "the scanned column holds no cave biome");
    }

    [Fact]
    public void AnEyeAtOrBelowTheBarrierIsPlainWithoutReadingAColumn()
    {
        var source = new CountingSource();
        var probe = new EyeCaveBiomeProbe();
        Assert.Equal(CaveBiome.Plain, probe.Update(source, Eye(3, WorldGenerationConfig.BarrierTopY, 5)));
        Assert.Equal(CaveBiome.Plain, probe.Update(source, Eye(3, WorldGenerationConfig.BarrierBottomY - 500, 5)));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void TheColumnIsReadOnlyWhenTheEyeChangesCell()
    {
        var source = new CountingSource();
        var probe = new EyeCaveBiomeProbe();
        var y = CaveBiomes.SubBandTopY - 20;
        probe.Update(source, new Vector3(4.1f, y + 0.2f, 6.1f));
        probe.Update(source, new Vector3(4.9f, y + 0.9f, 6.8f));
        probe.Update(source, new Vector3(4.5f, y + 0.5f, 6.5f));
        Assert.Equal(1, source.Reads);
        probe.Update(source, new Vector3(4.5f, y - 0.5f, 6.5f));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void AnEyeAboveTheSurfaceIsPlain()
    {
        var column = ColumnCache.Get(Cx, Cz);
        var i = (Lx << Chunk.SizeShift) | Lz;
        var probe = new EyeCaveBiomeProbe();
        var eye = Eye(Cx * Chunk.Size + Lx, column.Heights[i] + 1, Cz * Chunk.Size + Lz);
        Assert.Equal(CaveBiome.Plain, probe.Update(GeneratedWorldSource.Instance, eye));
    }

    private static Vector3 Eye(int x, int y, int z) => new(x + 0.5f, y + 0.5f, z + 0.5f);

    /// <summary>The generated source, counting its column reads.</summary>
    private sealed class CountingSource : IWorldSource
    {
        public int Reads { get; private set; }

        public ColumnData GetColumn(int chunkX, int chunkZ)
        {
            Reads++;
            return GeneratedWorldSource.Instance.GetColumn(chunkX, chunkZ);
        }

        public bool Supplies(int chunkX, int chunkZ) => GeneratedWorldSource.Instance.Supplies(chunkX, chunkZ);
        public void Fill(Chunk chunk) => GeneratedWorldSource.Instance.Fill(chunk);
        public bool IsReady(int chunkX, int chunkZ) => GeneratedWorldSource.Instance.IsReady(chunkX, chunkZ);

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
            GeneratedWorldSource.Instance.TryGet(chunkX, chunkZ, out terrain);
    }
}
