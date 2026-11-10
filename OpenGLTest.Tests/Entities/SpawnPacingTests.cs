using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// How the world keeps itself populated around moving players: the natural cap follows the players in play
/// (<see cref="MobCaps.NaturalCap"/>), an emptier world spawns faster (<see cref="MobCaps.SpawnHaste"/>), the
/// spawn ring leans toward a running player's heading (<see cref="MobSpawner.RingColumn"/>), a mob it leaves
/// far behind stops counting (<see cref="MobTargets.OutOfPlay"/>), and a target that only reports positions
/// gets its heading estimated (<see cref="MotionEstimate"/>).
/// </summary>
public sealed class SpawnPacingTests
{
    private sealed class Population(int living) : IMobPopulation
    {
        public int LivingCount { get; set; } = living;
    }

    private sealed class MovingTarget(Vector3 eye, Vector3 velocity) : IMobTarget
    {
        public Vector3 Eye => eye;
        public Vector3 Velocity => velocity;
        public bool IsImmune => false;
        public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null) { }
    }

    private static readonly Vector3 Running = new(MobConfig.MobSpawnAheadMinSpeed * 2f, 0f, 0f);

    [Fact]
    public void TheNaturalCapGrowsWithThePlayersUpToItsLimit()
    {
        var caps = new MobCaps();
        var alone = caps.NaturalCap;

        caps.Players = 2;
        Assert.Equal(2 * alone, caps.NaturalCap);

        caps.Players = MobConfig.MobNaturalCapMaxPlayers;
        var most = caps.NaturalCap;
        caps.Players = MobConfig.MobNaturalCapMaxPlayers + 5;
        Assert.Equal(most, caps.NaturalCap);
        Assert.True(most < MobConfig.MobHardCap, "the natural cap must leave room for forced spawns");

        // Nobody online spawns as for one player, never under it
        caps.Players = 0;
        Assert.Equal(alone, caps.NaturalCap);
    }

    [Fact]
    public void AnEmptierWorldSpawnsFaster()
    {
        var caps = new MobCaps();
        var population = new Population(0);
        caps.Register(population);

        var empty = caps.SpawnHaste;
        population.LivingCount = caps.NaturalCap / 2;
        var half = caps.SpawnHaste;
        population.LivingCount = caps.NaturalCap;
        var full = caps.SpawnHaste;
        population.LivingCount = caps.NaturalCap * 2;
        var over = caps.SpawnHaste;

        Assert.True(empty > half && half > full, $"haste {empty} / {half} / {full} should fall as the world fills");
        Assert.Equal(1f, full, 1e-5f);
        Assert.Equal(1f, over, 1e-5f);
    }

    [Fact]
    public void TheRingLeansTowardARunningTargetsHeading()
    {
        const int samples = 800;
        var ahead = AheadShare(new SpawnOrigin(Vector3.Zero, Running), samples);
        var atRest = AheadShare(Vector3.Zero, samples);
        var strolling = AheadShare(new SpawnOrigin(Vector3.Zero, Running * 0.2f), samples);

        // Half the circle lies ahead of any heading, so an unbiased ring puts about half its columns there
        Assert.InRange(atRest, 0.35f, 0.65f);
        Assert.InRange(strolling, 0.35f, 0.65f);
        Assert.True(ahead > 0.75f, $"only {ahead:P0} of a running target's ring fell ahead of it");
    }

    private static float AheadShare(SpawnOrigin origin, int samples)
    {
        var ahead = 0;
        for (var i = 0; i < samples; i++)
        {
            MobSpawner.RingColumn(origin, 40f, 64f, out var x, out _);
            if (x + 0.5f > 0f) ahead++;
        }
        return ahead / (float)samples;
    }

    [Fact]
    public void ARunningTargetLeavesFarMobsBehindButKeepsTheOnesAhead()
    {
        const float radius = 100f;
        var far = MobConfig.MobLeftBehindBlocks + 8f;
        var behind = new Vector3(-far, 0f, 0f);
        var aheadOfIt = new Vector3(far, 0f, 0f);
        var closeBehind = new Vector3(-MobConfig.MobLeftBehindBlocks * 0.5f, 0f, 0f);

        IMobTarget[] running = [new MovingTarget(Vector3.Zero, Running)];
        Assert.True(MobTargets.OutOfPlay(running, behind, radius));
        Assert.False(MobTargets.OutOfPlay(running, aheadOfIt, radius));
        Assert.False(MobTargets.OutOfPlay(running, closeBehind, radius));

        // Standing still, the same far mob stays; and a second player it is ahead of keeps it too
        IMobTarget[] standing = [new MovingTarget(Vector3.Zero, Vector3.Zero)];
        Assert.False(MobTargets.OutOfPlay(standing, behind, radius));
        IMobTarget[] two = [running[0], new MovingTarget(behind + new Vector3(-10f, 0f, 0f), Vector3.Zero)];
        Assert.False(MobTargets.OutOfPlay(two, behind, radius));

        // Past the despawn radius nothing keeps it, whichever way anybody moves
        Assert.True(MobTargets.OutOfPlay(standing, new Vector3(radius + 1f, 0f, 0f), radius));
    }

    [Fact]
    public void AMotionEstimateFollowsSteadyMovementAndRestartsOnATeleport()
    {
        var motion = new MotionEstimate();
        var velocity = new Vector3(5f, 0f, -2f);
        var position = Vector3.Zero;
        const float dt = 1f / 60f;

        // Positions arrive in bursts, as network states do: the estimate still settles on the true velocity
        for (var frame = 0; frame < 600; frame++)
        {
            if (frame % 3 == 0) position += velocity * (3f * dt);
            motion.Observe(position, dt);
        }
        Assert.True(Vector3.Distance(velocity, motion.Velocity) < 1.5f, $"estimated {motion.Velocity}");
        Assert.True(Vector3.Dot(Vector3.Normalize(velocity), Vector3.Normalize(motion.Velocity)) > 0.95f);

        motion.Observe(position + new Vector3(500f, 0f, 0f), dt);
        Assert.Equal(Vector3.Zero, motion.Velocity);
    }
}
