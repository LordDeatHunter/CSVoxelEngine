using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Simulation.Icebergs;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Icebergs;

/// <summary>
/// Where a berg may spawn (<see cref="IcebergSpawner"/>): open sea water inside the cold band at
/// the pack-ice edge, deep and wide enough for the whole berg, never on a river or lake at sea
/// level, never where a world source supplies the column (no worldgen climate there) and never
/// beside a player.
/// </summary>
public sealed class IcebergSpawnerTests
{
    public IcebergSpawnerTests() => TestContent.EnsureLoaded();

    private static readonly IcebergShape Small = IcebergShape.ForType(IcebergTestWorld.Type)[0];

    private static IcebergClimate Climate(float cold, bool frozen) =>
        (ChunkManager _, int _, int _, int _, out float c, out bool f) =>
        {
            c = cold;
            f = frozen;
            return true;
        };

    private static float InBandCold => (IcebergConfig.SpawnColdMin + WorldGenerationConfig.FreezeColdThreshold) * 0.5f;

    private static bool SiteAt(IcebergTestWorld world, int x, int z, IcebergClimate climate,
        IReadOnlyList<IMobTarget>? targets = null) =>
        IcebergSpawner.TrySiteAt(world.Chunks, x, z, Small, 0, 1f, targets ?? [], climate, out _);

    [Fact]
    public void ASiteIsFoundOnlyOnOpenWaterInsideTheBand()
    {
        var world = IcebergTestWorld.Create(ice: true);

        Assert.True(IcebergSpawner.TrySiteAt(world.Chunks, 0, 0, Small, 0, 1f, [], Climate(InBandCold, false),
            out var center));
        var placed = new List<(Vector3 Min, Vector3 Max)>();
        Small.PlaceAll(0, 1f, center, placed);
        foreach (var (min, max) in placed)
            Assert.True(IcebergManager.CellsClear(world.Chunks, min, max));

        Assert.False(SiteAt(world, 0, 0, Climate(InBandCold, true)));
        Assert.False(SiteAt(world, 0, 0, Climate(WorldGenerationConfig.FreezeColdThreshold + 0.1f, false)));
        Assert.False(SiteAt(world, 0, 0, Climate(IcebergConfig.SpawnColdMin - 0.1f, false)));
        // A footprint reaching over the ice strip
        Assert.False(SiteAt(world, IcebergTestWorld.IceMinX - 1, 0, Climate(InBandCold, false)));
    }

    [Fact]
    public void ANonOceanColumnAtSeaLevelIsRejected()
    {
        // The same water described as a river's (a forest column at sea level)
        var world = IcebergTestWorld.Create(ice: false, biome: Biome.Forest);
        Assert.False(SiteAt(world, 0, 0, Climate(InBandCold, false)));
    }

    [Fact]
    public void ASuppliedColumnHasNoWorldgenClimate()
    {
        var world = IcebergTestWorld.Create(ice: false);
        Assert.False(SiteAt(world, 0, 0, IcebergSpawner.WorldgenClimate));
        Assert.False(IcebergSpawner.TryFindSite(world.Chunks, Vector3.Zero, Small, 0, 1f, [], new Random(1), out _));
    }

    [Fact]
    public void NoSiteNextToAPlayer()
    {
        var world = IcebergTestWorld.Create(ice: false);
        var climate = Climate(InBandCold, false);
        Assert.True(SiteAt(world, 0, 0, climate));
        var near = new StubTarget(new Vector3(IcebergConfig.SpawnClearRadiusBlocks * 0.5f, 5f, 0f));
        Assert.False(SiteAt(world, 0, 0, climate, [near]));
    }
}
