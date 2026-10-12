using System.Numerics;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Torch-safe ground (<see cref="MobSpawner.IsLitByBlocks"/>): a ring spawn never lands where block
/// light reaches the limit, while darker or unlit ground still takes one.
/// </summary>
public sealed class TorchSafeSpawnTests
{
    public TorchSafeSpawnTests() => TestContent.EnsureLoaded();

    // Publishes the same light (sun and block, 0..15 each) for every cell of every loaded chunk.
    private static void LightEverything(ChunkManager chunks, int sun, int block)
    {
        foreach (var chunk in chunks.Chunks.Values)
        {
            var packed = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
            for (var i = 0; i < packed.Length; i++)
                packed[i] = (byte)((sun << 4) | block);
            chunk.PublishLight(packed);
        }
    }

    [Fact]
    public void BlockLightAtTheLimitMarksACellLitAndBelowItDoesNot()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        LightEverything(chunks, 15, WorldGenerationConfig.MobSpawnBlockLightLimit);
        Assert.True(MobSpawner.IsLitByBlocks(chunks, 4, 1, 4));

        LightEverything(chunks, 15, WorldGenerationConfig.MobSpawnBlockLightLimit - 1);
        Assert.False(MobSpawner.IsLitByBlocks(chunks, 4, 1, 4));

        // Sunlight alone never counts
        LightEverything(chunks, 15, 0);
        Assert.False(MobSpawner.IsLitByBlocks(chunks, 4, 1, 4));
    }

    [Fact]
    public void ARingNeverSpawnsOnTorchLitGroundButStillDoesOnDarkGround()
    {
        var chunks = MobTestWorld.Surface();
        var player = new Vector3(0.5f, 100f, 0.5f);

        LightEverything(chunks, 15, 15);
        for (var attempt = 0; attempt < 60; attempt++)
            Assert.False(MobSpawner.TryFindSurfaceSpawn(chunks, player, 20f, 40f, 8, out _, out _, out _),
                "a mob spawned on torch-lit ground");

        LightEverything(chunks, 15, 0);
        var found = false;
        for (var attempt = 0; attempt < 60 && !found; attempt++)
            found = MobSpawner.TryFindSurfaceSpawn(chunks, player, 20f, 40f, 8, out _, out _, out _);
        Assert.True(found, "no spawn found on unlit ground");
    }
}
