using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Slime splitting: a slime larger than the smallest size splits into children of the next smaller
/// size exactly when a hit takes it from above half its health to at or below half while it
/// survives. A killing blow, a hit below half and the smallest slime never split, and a split never
/// takes the population past the hard mob cap.
/// </summary>
public sealed class SlimeSplitTests
{
    public SlimeSplitTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType SlimeType => EntityRegistry.Require("slime");

    private static Vector3 GroundBody(float x, float z, int size) => new(x, 1f + Slime.HalfExtentFor(size), z);

    // A player well clear of the slimes on the floor, so nothing touches or despawns them.
    private static Camera FarPlayer()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(14.5f, 1f + Camera.EyeHeight, 14.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    // The damage that takes a slime from where it stands to exactly half its pool (rounded down)
    private static int ToHalf(Slime slime) => slime.Health - slime.MaxHealth / 2;

    [Fact]
    public void OnlyAHitAcrossTheHalfLineThatTheSlimeSurvivesSplits()
    {
        const int max = 20;
        var large = SlimeSize.Max;
        Assert.True(SlimeSize.SplitsOnHit(max, max / 2, max, large));
        Assert.True(SlimeSize.SplitsOnHit(max / 2 + 1, 1, max, large));
        Assert.False(SlimeSize.SplitsOnHit(max, max / 2 + 1, max, large)); // still above half
        Assert.False(SlimeSize.SplitsOnHit(max, 0, max, large));           // a killing blow
        Assert.False(SlimeSize.SplitsOnHit(max / 2, max / 2 - 1, max, large)); // already at half
        Assert.False(SlimeSize.SplitsOnHit(max, max / 2, max, SlimeSize.Min)); // too small
    }

    [Fact]
    public void AKillingBlowFromFullHealthNeverSplits()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var manager = new SlimeManager(SlimeType);
        var slime = manager.Spawn(GroundBody(6.5f, 6.5f, SlimeSize.Max), SlimeSize.Max)!;

        slime.TakeDamage(slime.MaxHealth);
        manager.Update(Dt, chunks, new DropManager(), FarPlayer());

        Assert.True(slime.IsDying);
        Assert.Same(slime, Assert.Single(manager.Slimes));
        Assert.Equal(0, manager.LivingCount);
    }

    [Fact]
    public void AHitAcrossTheHalfLineSplitsOnceAndASecondHitBelowHalfDoesNot()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var camera = FarPlayer();
        var manager = new SlimeManager(SlimeType);
        var parent = manager.Spawn(GroundBody(6.5f, 6.5f, SlimeSize.Max), SlimeSize.Max)!;
        var parentAt = parent.Position;

        parent.TakeDamage(ToHalf(parent));
        manager.Update(Dt, chunks, drops, camera);

        // The parent lives on at half health beside its children, each the next smaller size at full health
        Assert.False(parent.IsDead);
        var children = manager.Slimes.Where(s => s != parent).ToList();
        Assert.Equal(WorldGenerationConfig.SlimeSplitChildren, children.Count);
        foreach (var child in children)
        {
            Assert.Equal(parent.Size - 1, child.Size);
            Assert.Equal(child.MaxHealth, child.Health);
            // Shoved outward from where the parent stood
            var outward = new Vector3(child.Position.X - parentAt.X, 0f, child.Position.Z - parentAt.Z);
            var push = new Vector3(child.Velocity.X, 0f, child.Velocity.Z);
            Assert.True(push.LengthSquared() > 0f);
            Assert.True(Vector3.Dot(outward, push) > 0f, "a child was shoved back into the parent");
            Assert.False(Slime.IsColliding(child.Position, child.BodyHalfExtent, chunks));
        }

        // Below half, a further hit that it survives splits nothing more
        var count = manager.Slimes.Count;
        parent.TakeDamage(1);
        Assert.False(parent.IsDead);
        manager.Update(Dt, chunks, drops, camera);
        Assert.Equal(count, manager.Slimes.Count);
    }

    [Fact]
    public void TheSmallestSlimeNeverSplits()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var manager = new SlimeManager(SlimeType);
        var slime = manager.Spawn(GroundBody(6.5f, 6.5f, SlimeSize.Min), SlimeSize.Min)!;

        slime.TakeDamage(ToHalf(slime));
        Assert.False(slime.IsDead);
        manager.Update(Dt, chunks, new DropManager(), FarPlayer());

        Assert.Same(slime, Assert.Single(manager.Slimes));
    }

    [Fact]
    public void ASplitNeverTakesThePopulationPastTheHardCap()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = FarPlayer();
        var caps = new MobCaps();
        var slimes = new SlimeManager(SlimeType, caps);
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"), caps);
        var goblinBody = new Vector3(14.5f, 1f + Goblin.Height * 0.5f, 2.5f);

        // Room for one child only: the first split makes exactly that one
        var tight = slimes.Spawn(GroundBody(4.5f, 6.5f, SlimeSize.Max), SlimeSize.Max)!;
        var full = slimes.Spawn(GroundBody(9.5f, 6.5f, SlimeSize.Max), SlimeSize.Max)!;
        while (caps.ForcedRoom > 1)
            goblins.Spawn(goblinBody, ammo: 0);
        tight.TakeDamage(ToHalf(tight));
        slimes.Update(Dt, chunks, new DropManager(), camera);
        Assert.Equal(3, slimes.LivingCount);
        Assert.Equal(WorldGenerationConfig.MobHardCap, caps.Living);

        // At the hard cap a split makes no children at all
        full.TakeDamage(ToHalf(full));
        slimes.Update(Dt, chunks, new DropManager(), camera);
        Assert.Equal(3, slimes.LivingCount);
        Assert.Equal(WorldGenerationConfig.MobHardCap, caps.Living);
    }
}
