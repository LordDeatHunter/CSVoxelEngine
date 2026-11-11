using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Camps;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Camps;

/// <summary>
/// Goblin camps: a camp is its lit fire first (no fire, no goblins), then goblins on the ring round it, each carrying
/// a torch and wandering about the fire; and only a dark night pitches one, at most one a night, the day clearing it.
/// </summary>
public sealed class GoblinCampTests
{
    public GoblinCampTests() => TestContent.EnsureLoaded();

    private static GoblinManager Goblins() => new(EntityRegistry.Require("goblin"));

    // A dry column of the surface test world near its middle, with its ground height
    private static (int X, int Y, int Z) DryColumn()
    {
        for (var x = -8; x <= 8; x++)
        for (var z = -8; z <= 8; z++)
        {
            var height = TreeGenerator.HeightAt(x, z);
            if (!TerrainGenerator.IsWaterColumn(x, z, height)) return (x, height, z);
        }
        throw new Xunit.Sdk.XunitException("no dry column near the origin");
    }

    [Fact]
    public void ACampIsItsFireThenTorchBearingGoblinsMillingRoundIt()
    {
        var chunks = MobTestWorld.Surface();
        var (x, y, z) = DryColumn();

        var refused = Goblins();
        Assert.Equal(-1, new GoblinCamps(refused, (_, _, _) => false, () => 0.5f).Pitch(chunks, x, y, z));
        Assert.Empty(refused.Goblins);

        var goblins = Goblins();
        var fires = new List<(int, int, int)>();
        var risen = new GoblinCamps(goblins, (fx, fy, fz) =>
        {
            fires.Add((fx, fy, fz));
            return true;
        }, () => 0.99f).Pitch(chunks, x, y, z);

        Assert.Equal([(x, y, z)], fires);
        Assert.InRange(risen, 1, MobConfig.GoblinCampMaxGoblins);
        Assert.Equal(risen, goblins.Goblins.Count);
        var fire = new Vector3(x + 0.5f, y + 1f, z + 0.5f);
        foreach (var goblin in goblins.Goblins)
        {
            Assert.Equal(fire, goblin.WanderAnchor);
            Assert.True(goblin.Torches > 0, "a camp goblin carries no torch");
            var reach = new Vector2(goblin.Position.X - fire.X, goblin.Position.Z - fire.Z).Length();
            Assert.True(reach <= MobConfig.GoblinCampRingMaxBlocks + 1f, $"a camp goblin stands {reach} blocks out");
        }
    }

    [Fact]
    public void OnlyADarkNightPitchesACampAndAtMostOneANight()
    {
        var chunks = MobTestWorld.Surface();
        var goblins = Goblins();
        var pitched = 0;
        var camps = new GoblinCamps(goblins, (_, _, _) =>
        {
            pitched++;
            return true;
        }, () => 0f);
        // At the test world's west edge, so part of the far camp ring lies on loaded ground
        var (_, y, _) = DryColumn();
        IMobTarget[] targets = [new StubTarget(StubTarget.EyeAt(new Vector3(-48.5f, y + 1f, 0.5f)))];

        void RunFor(float seconds, float night)
        {
            for (var t = 0f; t < seconds; t += MobConfig.GoblinCampCheckSeconds)
                camps.Update(MobConfig.GoblinCampCheckSeconds + 0.01f, chunks, targets, night);
        }

        RunFor(MobConfig.GoblinCampCheckSeconds * 40f, 0f);
        Assert.Equal(0, pitched);

        RunFor(MobConfig.GoblinCampCheckSeconds * 60f, 1f);
        Assert.Equal(1, pitched);

        // The day clears the night's camp: the next night may pitch another
        RunFor(MobConfig.GoblinCampCheckSeconds, 0f);
        RunFor(MobConfig.GoblinCampCheckSeconds * 60f, 1f);
        Assert.Equal(2, pitched);
    }
}
