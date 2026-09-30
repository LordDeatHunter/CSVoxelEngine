using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The World lifecycle on the headless path: a storeless world plays at once, a store-backed one
/// loads (streaming only, input refused) until its ready sphere is meshed, restores the saved
/// player, then plays; <see cref="World.SaveAndDispose"/> writes the player, frees the world and
/// ends it, so the seed can change and a second world reads the save back.
/// </summary>
/// <remarks>
/// Every world here keeps the player at y = 1e6, as <c>ChunkGenerationWorkerTests</c> does: every
/// chunk the workers stream there is all air, so meshes are empty (a null pool can apply them),
/// and no mob can spawn naturally (the spawn search needs solid ground). Updates are driven with
/// the measured wall-clock delta, so the config timeouts mean real seconds, and
/// <see cref="World.LoadTimedOut"/> is asserted false so a load is proven by readiness.
/// </remarks>
[Collection("Worldgen seed")]
public sealed class WorldLifecycleTests : IDisposable
{
    private const float High = 1e6f;
    private const double LoadWallSeconds = 60;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly List<World> _worlds = [];

    public WorldLifecycleTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        // A failed assertion must not leave a live world behind: the next test's Apply would throw
        foreach (var world in _worlds)
            world.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
            // A teardown that timed out keeps the seed locked; the failing test already says why
        }

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private World NewWorld(ChunkSaveStore? store)
    {
        var world = new World(null!, null!, null!, null!, null!, null!, null, store);
        _worlds.Add(world);
        return world;
    }

    /// <summary>Updates <paramref name="world"/> with measured wall deltas (clamped to 0.1 s)
    /// until <paramref name="done"/> holds; false when <paramref name="wallSeconds"/> ran out.</summary>
    private static bool RunUntil(World world, Func<bool> done, double wallSeconds, Action? afterUpdate = null)
    {
        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (!done())
        {
            if (sw.Elapsed.TotalSeconds > wallSeconds)
                return false;
            var now = sw.Elapsed.TotalSeconds;
            world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            afterUpdate?.Invoke();
            Thread.Sleep(1);
        }

        return true;
    }

    private void WritePlayerFile(PlayerSaveData data)
    {
        var store = new ChunkSaveStore(_dir);
        store.EnqueueFile(PlayerSaveCodec.FileName, PlayerSaveCodec.ToBytes(data));
        Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
    }

    private void WriteEntitiesFile(EntitySaveData data)
    {
        var store = new ChunkSaveStore(_dir);
        store.EnqueueFile(EntitySaveCodec.FileName, EntitySaveCodec.ToBytes(data));
        Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
    }

    private EntitySaveData? ReadEntitiesFile()
    {
        var store = new ChunkSaveStore(_dir);
        var data = EntitySaveCodec.TryParse(store.ReadFile(EntitySaveCodec.FileName));
        Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
        return data;
    }

    /// <summary>Equips stone on hotbar slot 0 and selects it: the Inventory constructor stocks the
    /// pools but equips nothing, so a throw or a consume is a no-op until a slot is equipped.</summary>
    private static byte EquipRock(World world)
    {
        var id = TerrainGenerator.RockBlock;
        world.Inventory.EquipStackable(0, id);
        world.Inventory.Select(0);
        return id;
    }

    [Fact]
    public void StorelessWorldStartsPlayingAndTearsDown()
    {
        var world = NewWorld(null);
        Assert.Equal(WorldPhase.Playing, world.Phase);
        Assert.True(world.IsLoaded);
        Assert.Equal(1f, world.LoadProgress);

        var result = world.SaveAndDispose();
        Assert.True(result);
        Assert.Equal(WorldPhase.Disposed, world.Phase);
        Assert.False(WorldSeed.WorldIsLive);
        Assert.Equal(0, world.GetChunkManager().Chunks.Count);

        // Idempotent: the second call reports the first result and changes nothing
        Assert.Equal(result, world.SaveAndDispose());
        Assert.False(WorldSeed.WorldIsLive);
    }

    [Fact]
    public void StoreBackedWorldLoadsRestoresThePlayerThenPlays()
    {
        // An exported inventory with one block pool (not the stone thrown below) changed
        var source = new Inventory();
        var exported = source.Export();
        var changed = exported.Blocks!.First(s =>
            SaveNames.TryResolve(s.Name, out var id) && id != TerrainGenerator.RockBlock && s.Count > 1);
        changed.Count -= 1;
        Assert.True(SaveNames.TryResolve(changed.Name, out var changedId));
        var changedCount = changed.Count!.Value;
        Assert.NotEqual(source.CountFor(changedId), changedCount);

        WritePlayerFile(new PlayerSaveData
        {
            Position = [0f, High, 0f],
            Yaw = 12f,
            Pitch = -20f,
            Creative = true,
            Flying = true,
            Health = 42,
            LightingClock = 500,
            RespawnEye = [1f, High, 1f],
            Inventory = exported
        });

        var world = NewWorld(new ChunkSaveStore(_dir));
        Assert.Equal(WorldPhase.Loading, world.Phase);
        Assert.False(world.IsLoaded);
        Assert.Equal(new Vector3(0f, High, 0f), world.Camera.Position);
        Assert.Equal(12f, world.Camera.Yaw);
        Assert.Equal(-20f, world.Camera.Pitch);
        Assert.True(world.Camera.IsFlying && world.Camera.IsCreative);
        Assert.Equal(42, world.Health);
        Assert.Equal(changedCount, world.Inventory.CountFor(changedId));
        Assert.True(world.Camera.SuppressLook);

        // Nothing may edit a world that is still loading: the throw spends nothing and drops nothing
        var rock = EquipRock(world);
        var before = world.Inventory.CountFor(rock);
        Assert.True(before > 1);
        world.ThrowSelected();
        Assert.Empty(world.Drops);
        Assert.Equal(before, world.Inventory.CountFor(rock));
        Assert.False(world.TryBeginThrowCharge());
        Assert.False(world.TryUseSelected(World.InteractionReach));

        var progress = new List<float> { world.LoadProgress };
        Assert.True(RunUntil(world, () => world.IsLoaded, LoadWallSeconds, () => progress.Add(world.LoadProgress)),
            "the world did not finish loading");
        Assert.All(progress, p => Assert.InRange(p, 0f, 1f));
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i] >= progress[i - 1], $"load progress fell from {progress[i - 1]} to {progress[i]}");

        Assert.Equal(WorldPhase.Playing, world.Phase);
        Assert.False(world.LoadTimedOut, "the world started playing on the timeout, not on readiness");
        Assert.Equal(1f, world.LoadProgress);
        Assert.False(world.Camera.SuppressLook);
        Assert.True(Math.Abs(world.LightingTime - 500) < 1, $"lighting clock {world.LightingTime} did not resume");
        Assert.Equal(new Vector3(0f, High, 0f), world.Camera.Position);

        // Playing now: the same throw spends a unit and drops it
        world.ThrowSelected();
        Assert.Single(world.Drops);
        Assert.Equal(before - 1, world.Inventory.CountFor(rock));

        Assert.True(world.SaveAndDispose());
        Assert.False(WorldSeed.WorldIsLive);
        Assert.Equal(0, world.GetChunkManager().Chunks.Count);
        Assert.Empty(world.GetLodManager().RegionList);

        // The teardown ended the world: the seed may change and another world can be built on it
        WorldSeed.Apply(WorldSeed.Default + 5);
        var second = NewWorld(null);
        Assert.True(WorldSeed.WorldIsLive);
        Assert.True(second.SaveAndDispose());
        Assert.False(WorldSeed.WorldIsLive);
    }

    [Fact]
    public void SaveAndDisposeWritesThePlayerASecondWorldReadsBack()
    {
        // Only a position, so every other field takes its default
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });

        var first = NewWorld(new ChunkSaveStore(_dir));
        Assert.Equal(WorldPhase.Loading, first.Phase); // a store means loading, even for a fresh world
        Assert.Equal(new PlayerHealth().Health, first.Health);
        Assert.True(RunUntil(first, () => first.IsLoaded, LoadWallSeconds), "the first world did not finish loading");
        Assert.False(first.LoadTimedOut);

        var pose = new Vector3(5f, High + 3f, 7f);
        first.Camera.TestTeleport(pose, 45f, 10f);
        var rock = EquipRock(first);
        Assert.True(first.Inventory.ConsumeSelected());
        Assert.True(first.Inventory.ConsumeSelected());
        var rockCount = first.Inventory.CountFor(rock);
        var slot = first.Inventory.SlotAt(0);
        Assert.True(first.SaveAndDispose());
        Assert.False(WorldSeed.WorldIsLive);

        // A new store over the same folder: the first one was shut down by its world
        var second = NewWorld(new ChunkSaveStore(_dir));
        Assert.Equal(pose, second.Camera.Position);
        Assert.Equal(45f, second.Camera.Yaw);
        Assert.Equal(10f, second.Camera.Pitch);
        Assert.Equal(rockCount, second.Inventory.CountFor(rock));
        Assert.Equal(slot, second.Inventory.SlotAt(0));
        Assert.True(second.SaveAndDispose());
        Assert.False(WorldSeed.WorldIsLive);
    }

    [Fact]
    public void EntitiesRoundTripThroughSaveAndReload()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });

        var first = NewWorld(new ChunkSaveStore(_dir));
        Assert.True(RunUntil(first, () => first.IsLoaded, LoadWallSeconds), "the first world did not finish loading");
        Assert.False(first.LoadTimedOut);

        Assert.True(first.SpawnEntity("slime", new Vector3(2f, High - 1f, 2f)));
        Assert.True(first.SpawnEntity("goblin", new Vector3(-2f, High - 1f, 3f)));
        var slime = Assert.Single(first.Slimes);
        var goblin = Assert.Single(first.Goblins);
        // A wounded, turned and moving slime, so the restore cannot pass on spawn defaults
        var slimeHealth = slime.Type.MaxHealth - 1;
        var slimeVelocity = new Vector3(0.5f, 0f, -0.25f);
        slime.RestoreState(slimeHealth, 1.25f, slimeVelocity);
        // The body centre as the mob reports it, not the feet handed to SpawnEntity
        var slimeCentre = slime.Position;
        var slimeFacing = slime.Facing;
        var goblinCentre = goblin.Position;
        var goblinAmmo = goblin.Ammo;
        var goblinHealth = goblin.Health;

        var rock = EquipRock(first);
        first.ThrowSelected();
        var thrown = Assert.Single(first.Drops);
        var dropPosition = thrown.Position;
        var dropVelocity = thrown.Velocity;
        Assert.True(first.SaveAndDispose());

        var second = NewWorld(new ChunkSaveStore(_dir));
        Assert.Empty(second.Mobs); // nothing spawns before the flip
        Assert.True(RunUntil(second, () => second.IsLoaded, LoadWallSeconds), "the second world did not finish loading");
        Assert.False(second.LoadTimedOut);

        // Read right after the flipping Update: FinishLoading spawns before any manager has run,
        // and one more Update would let gravity pull everything down through the all-air chunks.
        var restoredSlime = Assert.Single(second.Slimes);
        Assert.Equal(slimeCentre, restoredSlime.Position);
        Assert.Equal(slimeHealth, restoredSlime.Health);
        Assert.Equal(slimeFacing, restoredSlime.Facing);
        Assert.Equal(slimeVelocity, restoredSlime.Velocity);

        var restoredGoblin = Assert.Single(second.Goblins);
        Assert.Equal(goblinCentre, restoredGoblin.Position);
        Assert.Equal(goblinAmmo, restoredGoblin.Ammo);
        Assert.Equal(goblinHealth, restoredGoblin.Health);
        Assert.Equal(2, second.Mobs.Count); // RefreshMobs ran at the flip

        var restoredDrop = Assert.Single(second.Drops);
        Assert.Equal(rock, restoredDrop.BlockId);
        Assert.Equal(dropPosition, restoredDrop.Position);
        Assert.Equal(dropVelocity, restoredDrop.Velocity);
        Assert.True(second.SaveAndDispose());
    }

    [Fact]
    public void AFarSavedDropWaitsForItsChunk()
    {
        var rock = SaveNames.NameOf(TerrainGenerator.RockBlock);
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        // Twelve chunks out: inside the load sphere, outside the ready sphere the flip waits for
        var far = new Vector3(200f, High, 0f);
        Assert.True(far.X / Chunk.Size > WorldGenerationConfig.LoadReadyRadiusChunks);
        Assert.True(far.X / Chunk.Size < WorldGenerationConfig.ChunkRadius);
        WriteEntitiesFile(new EntitySaveData
        {
            Drops =
            [
                new EntitySaveData.DropData { Item = rock, Position = [1f, High, 1f] },
                new EntitySaveData.DropData { Item = rock, Position = [far.X, far.Y, far.Z] }
            ]
        });

        var world = NewWorld(new ChunkSaveStore(_dir));
        var chunks = world.GetChunkManager();
        bool FarLoaded() => chunks.ChunkAt((int)far.X, (int)far.Y, (int)far.Z) != null;

        // The generation workers stream concurrently, so the far chunk's state is sampled on both
        // sides of the flipping Update: loaded before it means the flip must spawn the far drop,
        // still unloaded after it means the flip must have left it pending.
        var loadedBeforeFlip = false;
        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (!world.IsLoaded)
        {
            Assert.True(sw.Elapsed.TotalSeconds < LoadWallSeconds, "the world did not finish loading");
            loadedBeforeFlip = FarLoaded();
            var now = sw.Elapsed.TotalSeconds;
            world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }

        var loadedAfterFlip = FarLoaded();
        if (loadedBeforeFlip)
            Assert.Equal(2, world.Drops.Count);
        else if (!loadedAfterFlip)
            Assert.Single(world.Drops); // the far chunk is not loaded, so its drop is still pending

        Assert.True(RunUntil(world, () => world.Drops.Count == 2, LoadWallSeconds),
            "the far drop never spawned once its chunk streamed in");
        // Horizontal only: the drop has had one frame of gravity since it spawned
        Assert.Contains(world.Drops, d => MathF.Abs(d.Position.X - far.X) < 0.5f && MathF.Abs(d.Position.Z - far.Z) < 0.5f);
        Assert.True(world.SaveAndDispose());
    }

    [Fact]
    public void AFlushBeforeTheRestoreKeepsThePendingEntities()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        WriteEntitiesFile(new EntitySaveData
        {
            Mobs = [new EntitySaveData.MobData { Type = "slime", Position = [3f, High, 3f], Health = 1 }],
            Drops = [new EntitySaveData.DropData { Item = SaveNames.NameOf(TerrainGenerator.RockBlock), Position = [1f, High, 1f] }]
        });

        // Torn down while still loading: nothing was spawned, and the save must still hold both
        var world = NewWorld(new ChunkSaveStore(_dir));
        Assert.Equal(WorldPhase.Loading, world.Phase);
        Assert.True(world.SaveAndDispose());

        var saved = ReadEntitiesFile();
        Assert.NotNull(saved);
        var mob = Assert.Single(saved!.Mobs!);
        Assert.Equal("slime", mob.Type);
        Assert.Equal([3f, High, 3f], mob.Position);
        var drop = Assert.Single(saved.Drops!);
        Assert.Equal([1f, High, 1f], drop.Position);
    }

    [Fact]
    public void SavedMobsNeverExceedTheirKindsAliveCapAndTheSurplusStaysSaved()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        // More slimes than their kind may have alive, all inside the ready sphere, plus one goblin
        var mobs = new List<EntitySaveData.MobData>();
        for (var i = 0; i < WorldGenerationConfig.SlimeMaxAlive + 3; i++)
            mobs.Add(new EntitySaveData.MobData { Type = "slime", Position = [i, High, 0f] });
        mobs.Add(new EntitySaveData.MobData { Type = "goblin", Position = [0f, High, 2f] });
        WriteEntitiesFile(new EntitySaveData { Mobs = mobs });

        var world = NewWorld(new ChunkSaveStore(_dir));
        Assert.True(RunUntil(world, () => world.IsLoaded, LoadWallSeconds), "the world did not finish loading");
        Assert.False(world.LoadTimedOut);

        // Read right after the flipping Update, before any manager could move or despawn a mob
        Assert.True(world.Slimes.Count <= WorldGenerationConfig.SlimeMaxAlive,
            $"{world.Slimes.Count} slimes restored past the cap");
        Assert.Single(world.Goblins);
        Assert.Equal(world.Slimes.Count + world.Goblins.Count, world.Mobs.Count);
        Assert.True(world.Mobs.Count <= WorldGenerationConfig.MobsMaxAlive);

        // The live mobs plus the surplus still pending are all written back
        Assert.True(world.SaveAndDispose());
        var saved = ReadEntitiesFile();
        Assert.NotNull(saved);
        Assert.Equal(mobs.Count, saved!.Mobs!.Count);
    }

    [Fact]
    public void ASaveBeforeTheSpawnSettledKeepsNoPosition()
    {
        // No player file: the camera starts at the surface spawn, unsettled. Torn down with no
        // Update, so no mesh is ever applied through the null pools.
        var world = NewWorld(new ChunkSaveStore(_dir));
        Assert.True(world.SaveAndDispose());

        var store = new ChunkSaveStore(_dir);
        var saved = PlayerSaveCodec.TryParse(store.ReadFile(PlayerSaveCodec.FileName));
        Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
        Assert.NotNull(saved);
        Assert.Null(saved!.Position);
        // Everything but the pose is still written
        Assert.NotNull(saved.Creative);
        Assert.NotNull(saved.Flying);
    }

    [Fact]
    public void UnknownEntityNamesAreDroppedWithoutFailingTheLoad()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        WriteEntitiesFile(new EntitySaveData
        {
            Mobs =
            [
                new EntitySaveData.MobData { Type = "dragon", Position = [1f, High, 1f] },
                new EntitySaveData.MobData { Type = "slime", Position = [2f, High, 2f] }
            ],
            Drops = [new EntitySaveData.DropData { Item = "item/unobtainium", Position = [1f, High, 1f] }]
        });

        var world = NewWorld(new ChunkSaveStore(_dir));
        Assert.True(RunUntil(world, () => world.IsLoaded, LoadWallSeconds), "the world did not finish loading");
        Assert.Single(world.Slimes);
        Assert.Empty(world.Goblins);
        Assert.Empty(world.Drops);
        Assert.True(world.SaveAndDispose());
    }
}
