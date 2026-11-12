using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Battle tower sites built by hand from the loaded floor and altar templates, independent of the seed: the shell tests'
/// shapes (<see cref="SiteOf"/>) and the dressing tests' template pairings (<see cref="SyntheticSite"/>,
/// <see cref="SyntheticSites"/>). Content must be loaded (<see cref="TestContent.EnsureLoaded"/>).
/// </summary>
public static class TowerSiteFixture
{
    /// <summary>A tower of <paramref name="radius"/> and <paramref name="floors"/> at <paramref name="turns"/>, off the origin
    /// with a negative coordinate (so floor division and the world offsets are exercised), its floors cycling through the floor
    /// templates at turns 0, 1, 2, 3, ...</summary>
    public static TowerSite SiteOf(int radius, int floors, int turns)
    {
        var floorTemplates = StructureRegistry.Of(StructureKind.TowerFloor);
        var altar = StructureRegistry.Of(StructureKind.TowerAltar)[0];
        Assert.NotEmpty(floorTemplates);
        var picks = new StructureTemplate[floors];
        var floorTurns = new int[floors];
        for (var k = 0; k < floors; k++)
        {
            picks[k] = floorTemplates[k % floorTemplates.Count];
            floorTurns[k] = k & 3;
        }
        return new TowerSite(3, -2, 4611, -2987, 72, radius, floors, turns, TowerPalette.For(Biome.Forest), altar, 1, picks, floorTurns);
    }

    /// <summary>A tower of the smallest radius at a fixed spot, every floor wearing <paramref name="floor"/> and the roof
    /// <paramref name="altar"/>, both at <paramref name="turns"/>.</summary>
    public static TowerSite SyntheticSite(StructureTemplate floor, StructureTemplate altar, int turns)
    {
        var floors = WorldGenerationConfig.TowerMinFloors;
        return new TowerSite(5, 7, 5 * 1536 + 700, 7 * 1536 + 600, 70, WorldGenerationConfig.TowerMinRadius, floors, 0,
            TowerPalette.For(Biome.Forest), altar, turns, Enumerable.Repeat(floor, floors).ToArray(),
            Enumerable.Repeat(turns, floors).ToArray());
    }

    /// <summary>One synthetic site per (floor template, altar template, turn) pairing, so every template is seen at every turn.</summary>
    public static IEnumerable<TowerSite> SyntheticSites()
    {
        var floors = StructureRegistry.Of(StructureKind.TowerFloor);
        var altars = StructureRegistry.Of(StructureKind.TowerAltar);
        Assert.NotEmpty(floors);
        Assert.NotEmpty(altars);
        var pairs = Math.Max(floors.Count, altars.Count);
        for (var i = 0; i < pairs; i++)
        for (var turns = 0; turns < 4; turns++)
            yield return SyntheticSite(floors[i % floors.Count], altars[i % altars.Count], turns);
    }
}
