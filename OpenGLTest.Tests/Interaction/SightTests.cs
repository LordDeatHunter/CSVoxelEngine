using System.Numerics;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The sight-only ray walk (<see cref="BlockType.BlocksSightAt"/>): a punch, a slice or a goblin's
/// throw reaches through a plant, while the plant itself still aims for mining; stone stops both.
/// </summary>
public sealed class SightTests
{
    public SightTests() => TestContent.EnsureLoaded();

    private static ChunkManager Row(byte midId)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillAll(center, BlockType.Air);
        center.SetBlockRaw(2, 1, 1, midId, 0);
        return manager;
    }

    [Fact]
    public void APlantNeverStopsTheSightWalkButStillAims()
    {
        var plant = ContentRegistry.RequireBlock("tall_grass").Id;
        var manager = Row(plant);
        var from = new Vector3(1.5f, 1.5f, 1.5f);
        var to = new Vector3(3.5f, 1.5f, 1.5f);

        // A mob beyond the plant is in sight: the walk goes through it
        Assert.False(BlockRaycaster.SegmentHitsBlock(from, to, manager));

        // The aim ray still targets the plant itself (the box it enters)
        Assert.True(BlockRaycaster.Raycast(from, Vector3.UnitX, 5f, 0f, stopAtRange: false, manager,
            out var hit, out _, out _, out _, includeFluids: false));
        Assert.InRange(hit.X, 2f, 2.5f);
    }

    [Fact]
    public void EveryPlantIsSightless()
    {
        foreach (var name in new[] { "tall_grass", "dead_shrub", "flower", "cotton_plant" })
            Assert.False(BlockType.BlocksSightAt(ContentRegistry.RequireBlock(name).Id), name);
    }

    [Fact]
    public void StoneStillStopsTheSightWalk()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var manager = Row(stone);
        Assert.True(BlockRaycaster.SegmentHitsBlock(new Vector3(1.5f, 1.5f, 1.5f),
            new Vector3(3.5f, 1.5f, 1.5f), manager));

        // And a stone wall still aims as before
        Assert.True(BlockRaycaster.Raycast(new Vector3(1.5f, 1.5f, 1.5f), Vector3.UnitX, 5f, 0f,
            stopAtRange: false, manager, out _, out _, out _, out _, includeFluids: false));
    }
}
