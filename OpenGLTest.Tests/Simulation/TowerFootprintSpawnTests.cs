using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// No surface ring spawn inside a battle tower (BT-10): a tower's dark ground floor would pass every other check of
/// <see cref="MobSpawner.TryFindSurfaceSpawn(ChunkManager, Vector3, float, float, int, out int, out int, out int)"/>,
/// so a blocked column never spawns while the rest of the ring still does, and the footprint test the search uses
/// agrees with the tower site lookup.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerFootprintSpawnTests
{
    public TowerFootprintSpawnTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

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
    public void ARingNeverSpawnsInABlockedColumnButStillSpawnsOutside()
    {
        var chunks = MobTestWorld.Surface();
        LightEverything(chunks, 0, 0);
        var player = new Vector3(0.5f, 100f, 0.5f);

        var found = 0;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (!MobSpawner.TryFindSurfaceSpawn(chunks, player, 20f, 40f, 8, static (x, _) => x >= 0,
                    out var x, out _, out _))
                continue;
            found++;
            Assert.True(x < 0, $"a mob spawned in the blocked column at x {x}");
        }
        Assert.True(found > 0, "no spawn found in the open half of the ring");
    }

    [Fact]
    public void TheFootprintCheckMatchesTheTowerSiteLookup()
    {
        Assert.True(TowerPlacement.TryFindNearest(0, 0, 4, out var site), "no tower near the origin");
        var columns = new[]
        {
            (site.CenterX, site.CenterZ),
            (site.CenterX + site.Radius + 1, site.CenterZ),
            (site.MinX, site.CenterZ),
            (0, 0),
            (site.MaxX + 200, site.CenterZ),
        };
        foreach (var (x, z) in columns)
            Assert.Equal(TowerPlacement.TryFindAt(x, z, out _), TowerPlacement.Meets(x, z, x, z));

        // The relation is not vacuous: the tower's own center is inside, the origin is not
        Assert.True(TowerPlacement.Meets(site.CenterX, site.CenterZ, site.CenterX, site.CenterZ));
        Assert.False(TowerPlacement.Meets(0, 0, 0, 0));
    }
}
