using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// Floating damage numbers: the fixed pool (one number per hit, never more than its capacity, the
/// oldest replaced, each rising and fading out), a landed singleplayer hit showing exactly its
/// damage, and a client deriving the amount from the health drop between two server snapshots.
/// </summary>
[Collection("Worldgen seed")]
public sealed class DamageNumberTests : IDisposable
{
    private const float Dt = 1f / 60f;

    private readonly List<World> _worlds = [];

    public DamageNumberTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        foreach (var world in _worlds)
            world.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public void ASpawnShowsExactlyOneNumberOfItsAmount()
    {
        var pool = new DamageNumberPool(8);
        pool.Spawn(new Vector3(1f, 2f, 3f), 6);

        Assert.Equal(1, pool.Count);
        var shown = Active(pool);
        Assert.Single(shown);
        Assert.Equal(6, shown[0].Amount);
    }

    [Fact]
    public void ANonPositiveAmountShowsNothing()
    {
        var pool = new DamageNumberPool(8);
        pool.Spawn(Vector3.Zero, 0);
        pool.Spawn(Vector3.Zero, -3);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void AFullPoolReplacesItsOldestNumberAndNeverGrows()
    {
        const int capacity = 4;
        var pool = new DamageNumberPool(capacity);
        for (var amount = 1; amount <= 10; amount++)
        {
            pool.Spawn(Vector3.Zero, amount);
            pool.Update(Dt);
            Assert.True(pool.Count <= capacity);
            Assert.Equal(pool.Count, Active(pool).Count);
        }

        // The last `capacity` hits are the ones still showing
        var amounts = Active(pool).ConvertAll(n => n.Amount);
        amounts.Sort();
        Assert.Equal([7, 8, 9, 10], amounts);
    }

    [Fact]
    public void ANumberRisesAndFadesThenIsGone()
    {
        var pool = new DamageNumberPool(2);
        var origin = new Vector3(0f, 10f, 0f);
        pool.Spawn(origin, 2);

        var previousY = origin.Y;
        var previousAlpha = 1f;
        while (pool.Count > 0)
        {
            var number = Active(pool)[0];
            var y = DamageNumberPool.PositionOf(number).Y;
            var alpha = DamageNumberPool.AlphaOf(number);
            Assert.True(y >= previousY, "a number should only rise");
            Assert.True(alpha <= previousAlpha, "a number should only fade");
            previousY = y;
            previousAlpha = alpha;
            pool.Update(Dt);
        }
        Assert.True(previousY > origin.Y);
    }

    [Fact]
    public void ALandedPunchShowsOneNumberOfItsDamage()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var world = NewWorld();
        // High in open air, so nothing blocks the punch's line of sight
        var feet = new Vector3(0.5f, 200f, -2f);
        Assert.True(world.SpawnEntity("goblin", feet));
        var goblin = Assert.Single(world.Goblins);
        var healthBefore = goblin.Health;

        var player = new PlayerContext(Inventory.Empty())
        {
            Eye = new Vector3(0.5f, goblin.Position.Y, 0f),
            Forward = -Vector3.UnitZ,
            ViewDirection = -Vector3.UnitZ,
        };
        Assert.True(world.PunchFor(player));

        var dealt = healthBefore - goblin.Health;
        Assert.True(dealt > 0);
        Assert.Equal(1, world.DamageNumbers.Count);
        Assert.Equal(dealt, Active(world.DamageNumbers)[0].Amount);
    }

    [Fact]
    public void AHeadlessServerKeepsNoNumbers()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true);
        _worlds.Add(world);
        Assert.True(world.SpawnEntity("goblin", new Vector3(0.5f, 200f, -2f)));
        var goblin = Assert.Single(world.Goblins);
        var healthBefore = goblin.Health;

        world.DamageAllMobs(1);
        Assert.True(goblin.Health < healthBefore, "the hit itself still lands on a server");
        Assert.Equal(0, world.DamageNumbers.Count);
    }

    [Fact]
    public void AClientShowsTheHealthDropBetweenSnapshotsAndNothingForARise()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var world = NewWorld(clientMode: true);
        world.GetChunkManager().Headless = true;
        var near = world.Camera.Position + new Vector3(2f, 0f, 0f);

        // A newly listed mob is no hit
        world.ApplyNetworkEntities([Slime(7, near, 5)]);
        Assert.Equal(0, world.DamageNumbers.Count);

        world.ApplyNetworkEntities([Slime(7, near, 3)]);
        Assert.Equal(1, world.DamageNumbers.Count);
        Assert.Equal(2, Active(world.DamageNumbers)[0].Amount);

        // Healing back up shows nothing
        world.ApplyNetworkEntities([Slime(7, near, 4)]);
        Assert.Equal(1, world.DamageNumbers.Count);
    }

    [Fact]
    public void AClientShowsAKillAsTheHealthLeftOnlyNearTheCamera()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var world = NewWorld(clientMode: true);
        world.GetChunkManager().Headless = true;
        var near = world.Camera.Position + new Vector3(2f, 0f, 0f);
        var far = world.Camera.Position
                  + new Vector3(WorldGenerationConfig.DamageNumberDeathRangeBlocks * 2f, 0f, 0f);

        world.ApplyNetworkEntities([Slime(7, near, 4), Slime(8, far, 4)]);
        // The server stops listing both: the near one died, the far one is out of sight
        world.ApplyNetworkEntities([]);

        Assert.Equal(1, world.DamageNumbers.Count);
        Assert.Equal(4, Active(world.DamageNumbers)[0].Amount);
    }

    private World NewWorld(bool clientMode = false)
    {
        var world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: clientMode);
        _worlds.Add(world);
        return world;
    }

    private static NetEntity Slime(int id, Vector3 position, int health) =>
        new(NetEntityKind.Slime, id, position, 0f, health, 0, 0, Vector3.Zero);

    private static List<DamageNumber> Active(DamageNumberPool pool)
    {
        var active = new List<DamageNumber>();
        foreach (var number in pool.Items)
            if (number.Active)
                active.Add(number);
        return active;
    }
}
