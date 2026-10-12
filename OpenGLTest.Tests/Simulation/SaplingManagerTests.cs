using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The sapling clock (<see cref="SaplingManager"/>): a planted sapling pulses one growth step at a
/// time until its species' maturity step and never past it, an unloaded cell waits, and a sapling
/// whose wood is gone is forgotten.
/// </summary>
public sealed class SaplingManagerTests
{
    public SaplingManagerTests() => TestContent.EnsureLoaded();

    private static TreeSpecies Oak => TreeSpeciesRegistry.Require("oak");

    // One dirt floor with an oak sapling root (the root wood cell) at (8, 1, 8)
    private static ChunkManager Ground()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("dirt").Id);
        chunks.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, Oak.WoodId,
            WoodState.Encode(0, WoodState.ParentRoot, shell: false));
        return chunks;
    }

    private static TreeGrowthResult Grew(int x, int y, int z) =>
        new(TreeGrowthStatus.Grew, x, y, z, 1, 1, 0, 0);

    [Fact]
    public void ASaplingPulsesToMaturityAndNeverPastIt()
    {
        var chunks = Ground();
        var saplings = new SaplingManager();
        saplings.Schedule("oak", 8, 1, 8);

        var maturity = TreeGrower.MaturityStepsAt(Oak, 8, 1, 8, WorldGenerationConfig.Seed);
        Assert.True(maturity > 0);
        var pulses = 0;
        // Run well past the whole growth window (one pulse per jittered SaplingPulseSeconds)
        var seconds = maturity * WorldGenerationConfig.SaplingPulseSeconds * 2f + 60f;
        for (var t = 0f; t < seconds; t += 0.25f)
        {
            saplings.Update(0.25f, chunks, (species, x, y, z, steps) =>
            {
                Assert.Equal("oak", species);
                Assert.Equal(1, steps);
                pulses++;
                return Grew(x, y, z);
            }, new Random(7));
        }

        Assert.Equal(maturity, pulses);
        Assert.Equal(0, saplings.Count);
    }

    [Fact]
    public void ASaplingOnUnloadedGroundWaits()
    {
        var chunks = new ChunkManager(null!);
        var saplings = new SaplingManager();
        saplings.Schedule("oak", 8, 1, 8);

        var growCalls = 0;
        for (var t = 0f; t < 120f; t += 0.5f)
        {
            saplings.Update(0.5f, chunks, (_, x, y, z, _) =>
            {
                growCalls++;
                return Grew(x, y, z);
            }, new Random(3));
        }

        Assert.Equal(0, growCalls);
        Assert.True(saplings.Contains(8, 1, 8));
    }

    [Fact]
    public void ASaplingWhoseWoodIsGoneIsForgotten()
    {
        // A loaded floor with no sapling root in the cell
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("dirt").Id);
        var saplings = new SaplingManager();
        saplings.Schedule("oak", 8, 1, 8);

        for (var t = 0f; t < 30f; t += 0.5f)
            saplings.Update(0.5f, chunks, (_, x, y, z, _) => Grew(x, y, z), new Random(3));

        Assert.Equal(0, saplings.Count);
    }

    [Fact]
    public void SchedulingTheSameCellTwiceKeepsOneClock()
    {
        var saplings = new SaplingManager();
        saplings.Schedule("oak", 8, 1, 8);
        saplings.Schedule("oak", 8, 1, 8);
        Assert.Equal(1, saplings.Count);
    }
}
