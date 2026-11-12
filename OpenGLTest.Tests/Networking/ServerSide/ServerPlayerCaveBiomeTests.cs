using System.Numerics;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>
/// The server reads the cave biome at each player's eye (CB-3): <see cref="ServerPlayer.TickStatus"/> takes the
/// point query's biome for the eye's cell, marks the status dirty when it changes and only then, and reads plain
/// once the eye is back above the surface. In the "Worldgen seed" collection because it reseeds.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ServerPlayerCaveBiomeTests
{
    private const float Dt = 1f / 20f;
    private const int X = 8, Z = 8;

    public ServerPlayerCaveBiomeTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    [Fact]
    public void TheEyesCaveBiomeIsReadAndMarksTheStatusOnlyWhenItChanges()
    {
        var chunks = new ChunkManager(null!);
        var column = ColumnCache.Get(0, 0);
        var i = (X << Chunk.SizeShift) | Z;
        var surfaceEye = new Vector3(X + 0.5f, column.Heights[i] + 1.5f, Z + 0.5f);

        // A cell under the column whose cave biome is not plain
        int? caveY = null;
        var expected = CaveBiome.Plain;
        for (var y = CaveBiomes.SubBandTopY; y > WorldGenerationConfig.BarrierTopY && caveY == null; y--)
        {
            var biome = CaveBiomes.At(X, y, Z, column.Biomes[i], column.Heights[i]);
            if (biome == CaveBiome.Plain) continue;
            caveY = y;
            expected = biome;
        }
        Assert.NotNull(caveY);

        // Settle at the surface first, so nothing but the cave byte can mark the status below
        var player = new ServerPlayer(1, "P", surfaceEye);
        player.TickStatus(Dt, chunks);
        Assert.Equal(CaveBiome.Plain, player.EyeCaveBiome);
        player.StatusDirty = false;

        player.Position = new Vector3(X + 0.5f, caveY.Value + 0.5f, Z + 0.5f);
        player.TickStatus(Dt, chunks);
        Assert.Equal(expected, player.EyeCaveBiome);
        Assert.True(player.StatusDirty);

        player.StatusDirty = false;
        player.TickStatus(Dt, chunks);
        Assert.False(player.StatusDirty);

        player.Position = surfaceEye;
        player.TickStatus(Dt, chunks);
        Assert.Equal(CaveBiome.Plain, player.EyeCaveBiome);
        Assert.True(player.StatusDirty);
    }
}
