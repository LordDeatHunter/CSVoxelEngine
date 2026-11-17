using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>A thrown item that sticks (<c>throwSticks</c>, the javelin) stays where it landed: in terrain until
/// someone comes for it, in a mob until the mob is gone.</summary>
public sealed class StuckDropTests
{
    private const float Step = 1f / 60f;
    private static readonly Vector3 Far = new(0.5f, 200f, 0.5f);

    private readonly ushort _javelin;

    public StuckDropTests()
    {
        TestContent.EnsureLoaded();
        _javelin = ContentRegistry.RequireItem("javelin").Id;
    }

    // A javelin thrown along +X into a stone cell at x = 4, and the stuck drop it leaves
    private (ChunkManager Chunks, Chunk Center, DropManager Drops, ItemDrop Drop) ThrowIntoWall(float pickupDelay = 0.5f)
    {
        var (chunks, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(4, 0, 0, ContentRegistry.RequireBlock("stone").Id, 0);
        var projectile = new ThrownProjectile(_javelin, new Vector3(0.5f, 0.5f, 0.5f), new Vector3(20f, 0f, 0f), 6f, 1f);
        for (var i = 0; i < 120 && projectile.Update(Step, chunks, null, out _); i++) { }
        Assert.True(projectile.Landed);

        var drops = new DropManager();
        var drop = drops.SpawnStuck(_javelin, projectile.Position, projectile.Heading, pickupDelaySeconds: pickupDelay);
        Assert.NotNull(drop);
        return (chunks, center, drops, drop!);
    }

    [Fact]
    public void AJavelinInTheTerrainStaysPutTipFirstWithNobodyNear()
    {
        var (chunks, _, drops, drop) = ThrowIntoWall();
        var rest = drop.Position;

        for (var i = 0; i < 180; i++) drops.Update(Step, Far, chunks);

        Assert.True(drop.IsStuck);
        Assert.Equal(rest, drop.Position);
        // Along its flight, which had begun to tip down
        Assert.True(Vector3.Dot(drop.StuckHeading, Vector3.UnitX) > 0.9f);
        // Its tip reaches into the cell it hit while its middle stays outside it
        Assert.True(rest.X < 4f);
        Assert.True(rest.X + ThrownProjectile.TipReach(_javelin) > 4f);
    }

    [Fact]
    public void AJavelinInTheTerrainIsTakenBackOnceItsDelayIsOver()
    {
        var (chunks, _, drops, drop) = ThrowIntoWall(pickupDelay: 0.5f);
        var beside = drop.Position + new Vector3(0f, DropManager.CollectHeightOffset, 1f);

        // Within reach but still inside the delay: it stays in the wall
        for (var i = 0; i < 10; i++) drops.Update(Step, beside, chunks);
        Assert.True(drop.IsStuck);
        Assert.Contains(drop, drops.Drops);

        var taken = false;
        for (var i = 0; i < 300 && !taken; i++)
        {
            drops.Update(Step, beside, chunks);
            taken = drops.Collected.Length > 0;
        }
        Assert.True(taken);
        Assert.Equal(_javelin, drops.Collected[0]);
    }

    [Fact]
    public void RestoredTerrainDropKeepsItsSavedDelayAndHeading()
    {
        var (chunks, _, _, original) = ThrowIntoWall();
        var drops = new DropManager();
        const float savedAge = 0.2f;
        const float savedDelay = 0.5f;
        Assert.True(drops.SpawnRestoredStuck(_javelin, original.Position, original.StuckHeading,
            inBody: false, age: savedAge, pickupDelaySeconds: savedDelay));
        var restored = Assert.Single(drops.Drops);
        Assert.True(restored.IsStuck);
        Assert.Equal(original.Position, restored.Position);
        Assert.Equal(original.StuckHeading, restored.StuckHeading);

        var beside = restored.Position + new Vector3(0f, DropManager.CollectHeightOffset, 1f);
        for (var i = 0; i < 10; i++) drops.Update(Step, beside, chunks);
        Assert.True(restored.IsStuck);
        Assert.False(restored.IsAttracted);

        for (var i = 0; i < 40 && !restored.IsAttracted; i++) drops.Update(Step, beside, chunks);
        Assert.False(restored.IsStuck);
        Assert.True(restored.IsAttracted);
    }

    [Fact]
    public void AJavelinFallsWhenTheBlockItIsInIsGone()
    {
        var (chunks, center, drops, drop) = ThrowIntoWall();
        var rest = drop.Position;

        center.SetBlockRaw(4, 0, 0, 0, 0);
        for (var i = 0; i < 30; i++) drops.Update(Step, Far, chunks);

        Assert.False(drop.IsStuck);
        Assert.True(drop.Position.Y < rest.Y);
    }

    [Fact]
    public void AJavelinInAMobRidesItAndCannotBeTakenUntilTheMobIsGone()
    {
        var (chunks, _) = MeshTestWorld.WalledArea();
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(3f, 0.5f, 0.5f));
        var drops = new DropManager();
        var drop = drops.SpawnStuck(_javelin, slime.Position - Vector3.UnitX * 0.3f, Vector3.UnitX, slime,
            pickupDelaySeconds: 0f)!;
        var beside = slime.Position + new Vector3(0f, DropManager.CollectHeightOffset, 1f);
        var offset = Vector3.Distance(drop.Position, slime.Position);

        // The mob turns a quarter: the javelin swings around with its body
        slime.RestoreState(slime.Health, MathF.PI / 2f, Vector3.Zero);
        for (var i = 0; i < 120; i++)
        {
            drops.ReleaseOrphans([slime]);
            drops.Update(Step, beside, chunks);
        }
        Assert.True(drop.IsStuck);
        Assert.Empty(drops.Collected.ToArray());
        Assert.Equal(offset, Vector3.Distance(drop.Position, slime.Position), 3);
        Assert.True(MathF.Abs(Vector3.Dot(drop.StuckHeading, Vector3.UnitX)) < 0.01f);

        // Killed, the mob lets go of it
        slime.TakeDamage(slime.Health);
        drops.ReleaseOrphans([slime]);
        Assert.False(drop.IsStuck);
    }

    [Fact]
    public void AJavelinFallsOutOfAMobThatLeftTheWorld()
    {
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(3f, 0.5f, 0.5f));
        var drops = new DropManager();
        var drop = drops.SpawnStuck(_javelin, slime.Position, Vector3.UnitX, slime)!;

        drops.ReleaseOrphans([]);

        Assert.False(drop.IsStuck);
    }
}
