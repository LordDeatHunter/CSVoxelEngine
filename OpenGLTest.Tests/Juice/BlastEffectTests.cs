using System.Numerics;
using VoxelEngine.Juice;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Juice;

/// <summary>
/// The rules behind a TNT blast's side effects: the camera shake falls off with distance
/// (<see cref="ShakeFalloff"/>), and <see cref="TntManager"/> reports each detonation it set off.
/// </summary>
public sealed class BlastEffectTests
{
    public BlastEffectTests() => TestContent.EnsureLoaded();

    private const float Strength = 1f;
    private const float Near = 4f;
    private const float Range = 48f;

    [Fact]
    public void TheShakeNeverGrowsWithDistance()
    {
        var previous = float.MaxValue;
        for (var d = 0f; d <= Range + 8f; d += 0.25f)
        {
            var trauma = ShakeFalloff.Trauma(Strength, Near, Range, d);
            Assert.True(trauma <= previous, $"the shake grew at {d} blocks");
            previous = trauma;
        }
    }

    [Fact]
    public void TheShakeIsZeroFromItsRangeOnAndFullInsideTheNearRadius()
    {
        Assert.Equal(0f, ShakeFalloff.Trauma(Strength, Near, Range, Range));
        Assert.Equal(0f, ShakeFalloff.Trauma(Strength, Near, Range, Range * 3f));
        Assert.True(ShakeFalloff.Trauma(Strength, Near, Range, 0f) > ShakeFalloff.Trauma(Strength, Near, Range, Near * 3f));
        // A strength under the cap is honoured as is right at the event
        Assert.Equal(0.5f, ShakeFalloff.Trauma(0.5f, Near, Range, 0f), 3);
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0.6f, 0.3f, -0.74f)]
    public void TheBlastPushNeverGrowsWithDistanceAndIsZeroOutsideTheRadius(float dx, float dy, float dz)
    {
        var centre = new Vector3(3f, 7f, -2f);
        var direction = Vector3.Normalize(new Vector3(dx, dy, dz));
        var previous = float.MaxValue;
        for (var d = 0f; d <= TntManager.BlastRadius + 2f; d += 0.1f)
        {
            var speed = BlastImpulse.At(centre, centre + direction * d, TntManager.BlastRadius).Length();
            Assert.True(speed <= previous + 1e-5f, $"the push grew at {d} blocks");
            Assert.True(speed <= WorldGenerationConfig.BlastPushMaxSpeed + 1e-4f);
            if (d >= TntManager.BlastRadius)
                Assert.Equal(0f, speed);
            previous = speed;
        }
    }

    [Fact]
    public void TheBlastPushPointsAwayFromTheCentre()
    {
        var centre = new Vector3(0.5f, 10.5f, 0.5f);
        foreach (var offset in new[] { new Vector3(2f, 0f, 0f), new Vector3(-1f, 0.5f, 1.5f), new Vector3(0f, -2f, 0.5f) })
        {
            var impulse = BlastImpulse.At(centre, centre + offset, TntManager.BlastRadius);
            Assert.True(Vector3.Dot(impulse, offset) > 0f, $"pushed toward the centre from {offset}");
        }
        // Right on the centre it still goes somewhere: straight up
        Assert.True(BlastImpulse.At(centre, centre, TntManager.BlastRadius).Y > 0f);
    }

    [Fact]
    public void ABlastFlingsTheOtherPrimedTntTheDropsAndTheFallingCubesInRange()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var tntId = ContentRegistry.RequireBlock("tnt").Id;
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var tnt = new TntManager();
        var drops = new DropManager();
        var falling = new FallingBlockManager();
        tnt.Spawn(new Vector3(8, 8, 8), tntId, 0f);
        // Two blocks east, a long fuse: it survives the blast and is shoved east
        tnt.Spawn(new Vector3(10, 8, 8), tntId, 60f);
        var other = tnt.Primed[1];
        drops.SpawnAt(sand, new Vector3(8.5f, 8.5f, 10.5f));
        var drop = drops.Drops[0];
        var cube = falling.Spawn(new Vector3(6, 8, 8), sand);
        // Out of range: left alone
        drops.SpawnAt(sand, new Vector3(8.5f, 8.5f, 8.5f + TntManager.BlastRadius + 2f));
        var far = drops.Drops[1];

        var otherBefore = other.Velocity;
        var dropBefore = drop.Velocity;
        var cubeBefore = cube.Velocity;
        var farBefore = far.Velocity;
        tnt.Update(1e-4f, manager, new LodManager(null!), drops, new FluidManager(), falling);

        Assert.Single(tnt.Blasts);
        Assert.True(other.Velocity.X - otherBefore.X > 1f);
        Assert.True(drop.Velocity.Z - dropBefore.Z > 1f);
        Assert.True(cube.Velocity.X - cubeBefore.X < -1f);
        Assert.Equal(farBefore, far.Velocity);
    }

    [Fact]
    public void ADetonationIsReportedForTheUpdateThatSetItOff()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var tnt = new TntManager();
        tnt.Spawn(new Vector3(8, 8, 8), ContentRegistry.RequireBlock("tnt").Id, 0f);
        var lod = new LodManager(null!);
        var drops = new DropManager();
        var fluids = new FluidManager();
        var falling = new FallingBlockManager();

        tnt.Update(1e-4f, manager, lod, drops, fluids, falling);
        var blast = Assert.Single(tnt.Blasts);
        Assert.True(Vector3.Distance(blast, new Vector3(8.5f)) < 0.1f);

        tnt.Update(1e-4f, manager, lod, drops, fluids, falling);
        Assert.Empty(tnt.Blasts);
    }
}
