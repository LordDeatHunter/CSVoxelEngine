using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// Ledge caution (<see cref="LedgeProbe"/>, <see cref="Mob.IsLedgeAhead"/>): a mob never walks or hops
/// off a drop of more than a few blocks while its target is level with it, but follows a target that
/// stands down below.
/// </summary>
public sealed class LedgeCautionTests
{
    public LedgeCautionTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // Feet height on either plateau and in the trench
    private const float PlateauFeet = 9f;
    private const float TrenchFeet = 1f;

    // Two plateaus (stone up to y = 8, x 0..6 and x 11..15) either side of a trench x 7..10 whose
    // floor is y = 0, eight blocks down: far more than a ledge.
    private static ChunkManager Trench()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        {
            if (x is >= 7 and <= 10) continue;
            for (var y = 1; y <= 8; y++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, y, z, stone, 0);
        }
        return chunks;
    }

    [Fact]
    public void AShortDropIsAlwaysTakenAndALongOneOnlyTowardATargetBelow()
    {
        var max = (int)WorldGenerationConfig.MobLedgeMaxDropBlocks;
        for (var drop = 0; drop <= max; drop++)
        {
            Assert.True(LedgeProbe.IsSafe(drop, targetBelow: false));
            Assert.True(LedgeProbe.IsSafe(drop, targetBelow: true));
        }
        Assert.False(LedgeProbe.IsSafe(max + 1, targetBelow: false));
        Assert.True(LedgeProbe.IsSafe(max + 1, targetBelow: true));

        Assert.False(LedgeProbe.TargetBelow(10f, 10f));
        Assert.False(LedgeProbe.TargetBelow(10f, 9f));
        Assert.True(LedgeProbe.TargetBelow(10f, 10f - WorldGenerationConfig.MobLedgeMaxDropBlocks - 1f));
    }

    [Fact]
    public void TheProbeReadsTheDropToTheFirstGroundAndCountsTheUnknownAsBottomless()
    {
        var chunks = Trench();
        Assert.Equal(0, LedgeProbe.DropBelow(chunks, 3.5f, PlateauFeet, 8.5f, 10));
        Assert.Equal(8, LedgeProbe.DropBelow(chunks, 8.5f, PlateauFeet, 8.5f, 10));
        // Not loaded: as good as bottomless
        Assert.Equal(11, LedgeProbe.DropBelow(chunks, -3.5f, PlateauFeet, 8.5f, 10));
        // Deeper than the scan: likewise
        Assert.Equal(5, LedgeProbe.DropBelow(chunks, 8.5f, PlateauFeet, 8.5f, 4));
    }

    private static Goblin Walker(ChunkManager chunks, Vector3 targetEye, float seconds)
    {
        var goblin = new Goblin(EntityRegistry.Require("goblin"),
            new Vector3(3.5f, PlateauFeet + 0.02f + Goblin.Height * 0.5f, 8.5f), 0, spawnIn: false);
        var target = new Target(targetEye);
        for (var t = 0f; t < seconds; t += Dt)
            goblin.Update(Dt, chunks, target);
        return goblin;
    }

    private sealed class Target(Vector3 eye) : IMobTarget
    {
        public Vector3 Eye { get; } = eye;
        public bool IsImmune => true;
        public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null) { }
    }

    [Fact]
    public void AGoblinStopsAtTheEdgeOfATrenchItsTargetIsAcross()
    {
        var chunks = Trench();
        var goblin = Walker(chunks, new Vector3(13.5f, PlateauFeet + Camera.EyeHeight, 8.5f), 6f);
        var feet = goblin.Position.Y - Goblin.Height * 0.5f;
        Assert.True(feet > PlateauFeet - 0.1f, $"the goblin walked off the ledge (feet at {feet})");
        Assert.True(goblin.Position.X > 5f, "the goblin never walked up to the edge");
        Assert.Equal(goblin.MaxHealth, goblin.Health);
    }

    [Fact]
    public void AGoblinFollowsATargetDownIntoTheTrench()
    {
        var chunks = Trench();
        var goblin = Walker(chunks, new Vector3(9f, TrenchFeet + Camera.EyeHeight, 8.5f), 6f);
        var feet = goblin.Position.Y - Goblin.Height * 0.5f;
        Assert.True(feet < TrenchFeet + 0.1f, $"the goblin stayed up top (feet at {feet})");
    }

    [Fact]
    public void ASlimeNeverHopsIntoATrenchItsTargetIsAcross()
    {
        var chunks = Trench();
        for (var size = SlimeSize.Min; size <= SlimeSize.Max; size++)
        {
            var half = Slime.HalfExtentFor(size);
            var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(2.5f, PlateauFeet + 0.02f + half, 8.5f), size);
            var target = new Vector3(14.5f, PlateauFeet + Camera.EyeHeight, 8.5f);
            for (var t = 0f; t < 10f; t += Dt)
            {
                slime.Update(Dt, chunks, target);
                Assert.True(slime.Position.Y - half > PlateauFeet - 4f, $"a size {size} slime fell into the trench");
            }
        }
    }
}
