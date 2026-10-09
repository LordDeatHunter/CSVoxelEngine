using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The mob managers' target rules: a mob chases its nearest non-immune target (an immune one
/// only when nobody else is in play), despawns only past every target's radius, and a hit lands
/// on the target it touched and nobody else.
/// </summary>
public sealed class MobTargetTests
{
    public MobTargetTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // Eye of a player standing on the y = 0 floor.
    private const float StandingEyeY = 1f + Camera.EyeHeight;

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    /// <summary>A target at a fixed eye that records every hit it takes.</summary>
    private sealed class RecordingTarget(Vector3 eye, bool immune = false) : IMobTarget
    {
        public Vector3 Eye { get; } = eye;
        public bool IsImmune { get; } = immune;
        public List<int> Hits { get; } = [];

        public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift) =>
            Hits.Add(damage);

        public int DamageTaken
        {
            get
            {
                var sum = 0;
                foreach (var hit in Hits) sum += hit;
                return sum;
            }
        }
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        var d = HorizontalDistance(a, b);
        return d * d;
    }

    [Fact]
    public void NearestPrefersASurvivalTargetOverANearerImmuneOne()
    {
        var mob = new Vector3(0f, 1f, 0f);
        var nearFlyer = new RecordingTarget(new Vector3(2f, 5f, 0f), immune: true);
        var farSurvivor = new RecordingTarget(new Vector3(-6f, 1f, 0f));

        Assert.Equal(1, MobTargets.Nearest([nearFlyer, farSurvivor], mob));
        Assert.Equal(0, MobTargets.Nearest([farSurvivor, nearFlyer], mob));

        // With everyone immune the nearest one is still chased, so a lone hovering camera is faced.
        var farFlyer = new RecordingTarget(new Vector3(-6f, 1f, 0f), immune: true);
        Assert.Equal(0, MobTargets.Nearest([nearFlyer, farFlyer], mob));
        Assert.Equal(1, MobTargets.Nearest([farFlyer, nearFlyer], mob));

        Assert.Equal(-1, MobTargets.Nearest([], mob));

        // The despawn distance is horizontal and takes the nearer target, immune or not.
        Assert.Equal(HorizontalDistanceSquared(nearFlyer.Eye, mob),
            MobTargets.MinHorizontalDistanceSquared([nearFlyer, farSurvivor], mob), 3);
        Assert.Equal(HorizontalDistanceSquared(nearFlyer.Eye, mob),
            MobTargets.MinHorizontalDistanceSquared([farSurvivor, nearFlyer], mob), 3);
        Assert.Equal(float.PositiveInfinity, MobTargets.MinHorizontalDistanceSquared([], mob));
    }

    [Fact]
    public void DespawnMeasuresAgainstTheNearestTarget()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var radius = WorldGenerationConfig.SlimeDespawnRadiusBlocks;
        var body = new Vector3(8.5f, 1f + Slime.HalfExtent, 8.5f);

        // Past A's radius but inside B's: B keeps it alive.
        var farA = new RecordingTarget(new Vector3(body.X + radius + 4f, StandingEyeY, body.Z));
        var nearB = new RecordingTarget(new Vector3(body.X - (radius - 4f), StandingEyeY, body.Z));
        var kept = new SlimeManager(EntityRegistry.Require("slime"));
        var survivor = kept.Spawn(body);
        kept.Update(Dt, chunks, drops, [farA, nearB]);
        Assert.Contains(survivor, kept.Slimes);

        // Past every target's radius: culled.
        var farC = new RecordingTarget(new Vector3(body.X - (radius + 4f), StandingEyeY, body.Z));
        var culling = new SlimeManager(EntityRegistry.Require("slime"));
        var stray = culling.Spawn(body);
        culling.Update(Dt, chunks, drops, [farA, farC]);
        Assert.DoesNotContain(stray, culling.Slimes);

        // With nobody to measure against nothing despawns.
        var empty = new SlimeManager(EntityRegistry.Require("slime"));
        var alone = empty.Spawn(body);
        empty.Update(Dt, chunks, drops, Array.Empty<IMobTarget>());
        Assert.Contains(alone, empty.Slimes);
    }

    [Fact]
    public void AGoblinWalksAtTheSurvivalPlayerNotTheNearerFlyingOne()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = new GoblinManager(EntityRegistry.Require("goblin"));
        var goblin = manager.Spawn(new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0);

        var flyer = new Camera(null, null);
        flyer.TestTeleport(new Vector3(5.5f, StandingEyeY, 8.5f), yaw: 0f, pitch: 0f);
        flyer.TestSetFlight(true);
        var survivor = new Camera(null, null);
        survivor.TestTeleport(new Vector3(13.5f, StandingEyeY, 8.5f), yaw: 0f, pitch: 0f);
        Assert.True(HorizontalDistance(goblin.Position, flyer.Position)
                    < HorizontalDistance(goblin.Position, survivor.Position));

        var toSurvivor = HorizontalDistance(goblin.Position, survivor.Position);
        var toFlyer = HorizontalDistance(goblin.Position, flyer.Position);
        IReadOnlyList<IMobTarget> targets = [flyer, survivor];
        for (var i = 0; i < (int)(2f / Dt); i++)
            manager.Update(Dt, chunks, drops, targets);

        Assert.True(HorizontalDistance(goblin.Position, survivor.Position) < toSurvivor - 1f,
            "the goblin did not close on the survival player");
        Assert.True(HorizontalDistance(goblin.Position, flyer.Position) > toFlyer,
            "the goblin moved toward the immune flyer");
    }

    [Fact]
    public void ContactDamageLandsOnlyOnTheTouchedTarget()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = new SlimeManager(EntityRegistry.Require("slime"));

        var bystander = new RecordingTarget(new Vector3(2.5f, StandingEyeY, 8.5f));
        var touched = new RecordingTarget(new Vector3(12.5f, StandingEyeY, 8.5f));

        // An airborne slime hanging in the touched player's body, well clear of the floor.
        var slime = manager.Spawn(new Vector3(touched.Eye.X, 2f, touched.Eye.Z));
        Assert.True(slime.CanDealContactDamage);
        Assert.True(MobTargets.BodyTouches(touched.Eye, slime.Position, Slime.HalfExtent));
        Assert.False(MobTargets.BodyTouches(bystander.Eye, slime.Position, Slime.HalfExtent));

        manager.Update(Dt, chunks, drops, [bystander, touched]);

        Assert.Empty(bystander.Hits);
        Assert.Single(touched.Hits);
        Assert.True(manager.DamageDealtThisFrame > 0);
        Assert.Equal(touched.DamageTaken, manager.DamageDealtThisFrame);
    }
}
