using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Losing interest (<see cref="MobInterest"/>, <see cref="Mob.PickTarget"/>): a mob gives up a target it
/// has not seen for the whole sightless stretch and takes it back once seen, a new target starts the
/// count over, a mob chasing nobody wanders, and one idle long and far from everyone fades out.
/// </summary>
public sealed class MobInterestTests
{
    public MobInterestTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private sealed class Target(Vector3 eye) : IMobTarget
    {
        public Vector3 Eye { get; } = eye;
        public bool IsImmune => false;
        public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null) { }
    }

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

    // A stone floor with a wall across x = 8 from y = 1 to the chunk top.
    private static ChunkManager Walled()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var y = 1; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(8, y, z, Stone, 0);
        return chunks;
    }

    private static Slime SlimeAt(float x, float z) =>
        new(EntityRegistry.Require("slime"), new Vector3(x, 1.05f + Slime.HalfExtent, z));

    [Fact]
    public void AWalledOffTargetIsGivenUpAfterTheSightlessStretchAndWonBackOnceSeen()
    {
        var chunks = Walled();
        var slime = SlimeAt(4.5f, 8.5f);
        IMobTarget[] targets = [new Target(new Vector3(12.5f, 1f + Camera.EyeHeight, 8.5f))];

        var t = 0f;
        for (; t < WorldGenerationConfig.MobLoseInterestSeconds - 0.25f; t += Dt)
            Assert.Equal(0, slime.PickTarget(targets, Dt, chunks));
        for (; t < WorldGenerationConfig.MobLoseInterestSeconds + 0.5f; t += Dt)
            slime.PickTarget(targets, Dt, chunks);
        Assert.Equal(-1, slime.PickTarget(targets, Dt, chunks));
        Assert.True(slime.Interest.IsLost);

        // The wall comes down: the next sight check finds the line clear and the chase resumes
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var y = 1; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(8, y, z, 0, 0);
        var chased = -1;
        for (var s = 0f; s < WorldGenerationConfig.MobSightCheckSeconds + 0.1f && chased < 0; s += Dt)
            chased = slime.PickTarget(targets, Dt, chunks);
        Assert.Equal(0, chased);
    }

    [Fact]
    public void AClearLineKeepsTheChaseGoing()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var slime = SlimeAt(4.5f, 8.5f);
        IMobTarget[] targets = [new Target(new Vector3(12.5f, 1f + Camera.EyeHeight, 8.5f))];
        for (var t = 0f; t < WorldGenerationConfig.MobLoseInterestSeconds * 2f; t += Dt)
            Assert.Equal(0, slime.PickTarget(targets, Dt, chunks));
        Assert.Equal(0f, slime.Interest.IdleSeconds);
    }

    [Fact]
    public void ANewTargetStartsTheCountOver()
    {
        var chunks = Walled();
        var slime = SlimeAt(4.5f, 8.5f);
        IMobTarget[] first = [new Target(new Vector3(12.5f, 1f + Camera.EyeHeight, 8.5f))];
        IMobTarget[] second = [new Target(new Vector3(12.5f, 1f + Camera.EyeHeight, 6.5f))];
        var lose = WorldGenerationConfig.MobLoseInterestSeconds;

        for (var t = 0f; t < lose * 0.75f; t += Dt)
            slime.PickTarget(first, Dt, chunks);
        // Most of the stretch already spent on the first; the second gets a whole one of its own
        for (var t = 0f; t < lose * 0.75f; t += Dt)
            Assert.Equal(0, slime.PickTarget(second, Dt, chunks));
    }

    [Fact]
    public void OnlyALongIdleFarFromEveryTargetDespawns()
    {
        var far = WorldGenerationConfig.MobIdleDespawnMinDistanceBlocks + 1f;
        var near = WorldGenerationConfig.MobIdleDespawnMinDistanceBlocks - 1f;
        var long_ = WorldGenerationConfig.MobIdleDespawnSeconds + 0.1f;
        var short_ = WorldGenerationConfig.MobIdleDespawnSeconds - 0.1f;
        Assert.True(MobInterest.ShouldIdleDespawn(long_, far * far));
        Assert.False(MobInterest.ShouldIdleDespawn(short_, far * far));
        Assert.False(MobInterest.ShouldIdleDespawn(long_, near * near));
    }

    [Fact]
    public void AWanderGoalLiesWithinTheWanderRing()
    {
        var origin = new Vector3(5f, 3f, -7f);
        for (var a = 0f; a < 1f; a += 0.1f)
        for (var r = 0f; r < 1f; r += 0.1f)
        {
            var goal = MobInterest.WanderPoint(origin, a, r);
            var d = MathF.Sqrt((goal.X - origin.X) * (goal.X - origin.X) + (goal.Z - origin.Z) * (goal.Z - origin.Z));
            Assert.InRange(d, WorldGenerationConfig.MobWanderMinRadiusBlocks - 1e-3f,
                WorldGenerationConfig.MobWanderMaxRadiusBlocks + 1e-3f);
        }
    }

    // A slime sealed in a one-cell stone box at (x, 1, z) on the floor: it can neither move nor see out.
    private static void Box(ChunkManager chunks, int x, int z)
    {
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = 0; dy <= 2; dy++)
        for (var dz = -1; dz <= 1; dz++)
            if (dx != 0 || dy != 1 || dz != 0)
                chunk.SetBlockRaw(x + dx, dy, z + dz, Stone, 0);
    }

    [Fact]
    public void AnIdleSlimeFarFromItsTargetFadesOutAndANearerOneStays()
    {
        var chunks = MobTestWorld.Floor(Stone);
        Box(chunks, 2, 2);
        Box(chunks, 8, 2);
        var manager = new SlimeManager(EntityRegistry.Require("slime"));
        var drops = new DropManager();
        var target = new Target(new Vector3(28.5f, 1f + Camera.EyeHeight, 2.5f));
        // 26 and 20 blocks off: both inside the notice range, one past the idle despawn distance
        var farSlime = manager.Spawn(new Vector3(2.5f, 1.02f + Slime.HalfExtent, 2.5f))!;
        var nearSlime = manager.Spawn(new Vector3(8.5f, 1.02f + Slime.HalfExtent, 2.5f))!;

        var total = WorldGenerationConfig.MobLoseInterestSeconds + WorldGenerationConfig.MobIdleDespawnSeconds + 1f;
        for (var t = 0f; t < total && !farSlime.IsDying; t += Dt)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.True(farSlime.IsDying, "the idle slime far from its target never despawned");
        Assert.False(nearSlime.IsDying, "the idle slime near its target despawned");
        Assert.Empty(drops.Drops);
    }

    [Fact]
    public void AMobChasingNobodyWandersButStandsStillWithNobodyInPlay()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var unnoticed = new Target(new Vector3(8.5f + WorldGenerationConfig.MobNoticeRadiusBlocks + 8f,
            1f + Camera.EyeHeight, 8.5f));

        var wanderer = new GoblinManager(EntityRegistry.Require("goblin"));
        var walker = wanderer.Spawn(new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0)!;
        var idle = new GoblinManager(EntityRegistry.Require("goblin"));
        var stander = idle.Spawn(new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0)!;
        var drops = new DropManager();
        var start = walker.Position;
        for (var t = 0f; t < 4f; t += Dt)
        {
            wanderer.Update(Dt, chunks, drops, [unnoticed]);
            idle.Update(Dt, chunks, drops, Array.Empty<IMobTarget>());
        }

        float Moved(Mob mob) => MathF.Sqrt((mob.Position.X - start.X) * (mob.Position.X - start.X)
                                           + (mob.Position.Z - start.Z) * (mob.Position.Z - start.Z));
        Assert.True(Moved(walker) > 0.5f, "a goblin chasing nobody never wandered");
        Assert.True(Moved(stander) < 0.05f, "a goblin with nobody in play wandered");

        var slimes = new SlimeManager(EntityRegistry.Require("slime"));
        var slime = slimes.Spawn(new Vector3(8.5f, 1.02f + Slime.HalfExtent, 8.5f))!;
        var slimeStart = slime.Position;
        for (var t = 0f; t < 4f; t += Dt)
            slimes.Update(Dt, chunks, drops, [unnoticed]);
        var slimeMoved = MathF.Sqrt((slime.Position.X - slimeStart.X) * (slime.Position.X - slimeStart.X)
                                    + (slime.Position.Z - slimeStart.Z) * (slime.Position.Z - slimeStart.Z));
        Assert.True(slimeMoved > 0.5f, "a slime chasing nobody never hopped off to wander");
    }
}
