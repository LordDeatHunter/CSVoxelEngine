using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Saves;

/// <summary>
/// A world's persistence: the autosave pass, the full flush a teardown runs, the player and
/// entities captured for the save store, and the saved mobs and drops restored as their chunks
/// load. Owned by <see cref="World"/>; main thread only.
/// </summary>
internal sealed class WorldPersistence
{
    private readonly World _world;
    private readonly ChunkSaveStore? _saveStore;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly SlimeManager? _slimes;
    private readonly GoblinManager? _goblins;
    private readonly PlayerHealth _health;
    private readonly bool _serverMode;

    private WorldPhase Phase => _world.Phase;
    private double LightingTime => _world.LightingTime;
    private Camera Camera => _world.Camera;
    private Inventory Inventory => _world.Inventory;
    private IReadOnlyList<Slime> Slimes => _world.Slimes;
    private IReadOnlyList<Goblin> Goblins => _world.Goblins;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;

    internal WorldPersistence(World world, ChunkSaveStore? saveStore, ChunkManager chunkManager,
        DropManager dropManager, SlimeManager? slimes, GoblinManager? goblins, PlayerHealth health, bool serverMode,
        PlayerSaveData? savedPlayer, EntitySaveData? savedEntities)
    {
        _world = world;
        _saveStore = saveStore;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _slimes = slimes;
        _goblins = goblins;
        _health = health;
        _serverMode = serverMode;
        _savedPlayer = savedPlayer;
        if (serverMode)
        {
            // The camera is the server's chunk focus, not a player: the player.json this world
            // was opened with is kept and written back unchanged (see CapturePlayer), so a
            // survival world hosted once does not reopen in creative flight at the centroid.
            _hostedPlayer = _savedPlayer;
        }
        if (savedEntities?.Mobs != null)
            _pendingMobs.AddRange(savedEntities.Mobs);
        if (savedEntities?.Drops != null)
            _pendingDrops.AddRange(savedEntities.Drops);
    }

    /// <summary>The player.json this world was opened with, until the Loading -> Playing flip forgets it.</summary>
    internal PlayerSaveData? SavedPlayer => _savedPlayer;

    /// <summary>Forgets the saved player once the flip has applied its lighting clock.</summary>
    internal void ForgetSavedPlayer() => _savedPlayer = null;

    /// <summary>Advances the autosave clock and runs an autosave pass when it is due; nothing without a save store.</summary>
    internal void UpdateAutosave(double deltaTime)
    {
        if (_saveStore != null)
        {
            _autosaveTimer += deltaTime;
            if (_autosaveTimer >= WorldGenerationConfig.AutosaveIntervalSeconds)
                Autosave();
        }
    }

    // The player.json this world was opened with, kept until the Loading -> Playing flip has
    // applied its lighting clock; null when the world has no saved player
    private PlayerSaveData? _savedPlayer;

    // Server mode only: the player.json the hosted world was opened with (null for a new world),
    // kept for the whole session because the server camera is no player and must never be saved
    // as one. CapturePlayer writes it back with only the clock refreshed (players keep their own
    // inventories, see ServerPlayer).
    private readonly PlayerSaveData? _hostedPlayer;

    // Saved mobs and drops (entities.json) not spawned yet: each waits for its chunk to load,
    // and the lot is discarded after EntityRestoreTimeoutSeconds of play (RestorePendingEntities)
    private readonly List<EntitySaveData.MobData> _pendingMobs = [];
    private readonly List<EntitySaveData.DropData> _pendingDrops = [];
    private double _entityRestoreTimer;

    // Scratch for CaptureEntities' nearest-first drop order, reused across saves
    private readonly List<(float dist2, ItemDrop drop)> _dropSort = [];

    // Seconds since the last completed autosave pass
    private double _autosaveTimer;

    // LodEditState.CombinedVersion when lod-edits.bin was last enqueued; -1 so the first
    // autosave always writes it
    private int _lodStateVersionSaved = -1;

    /// <summary>
    /// Spawns every pending saved mob and drop whose chunk is loaded, through the managers' own
    /// spawn paths, and restores its health, facing and motion. Each waits for its chunk because
    /// an entity restored over an unloaded chunk would fall through air (slimes, goblins and drops
    /// all read unloaded cells as air). A restore is a forced spawn: it may pass the natural mob cap
    /// but a mob waits the same way while the hard cap (<see cref="WorldGenerationConfig.MobHardCap"/>,
    /// which sizes the healthbar buffers) is full, so an over-full save never overfills the world
    /// and loses nothing, since pending entries are re-saved. An unknown entity kind or item name, or a record with no
    /// position, is dropped with one warning per pass. After
    /// <see cref="WorldGenerationConfig.EntityRestoreTimeoutSeconds"/> of play whatever is still
    /// pending is discarded. Main thread, Playing only; nothing pending costs one check.
    /// </summary>
    internal void RestorePendingEntities(double deltaTime)
    {
        if (_pendingMobs.Count == 0 && _pendingDrops.Count == 0)
            return;
        _entityRestoreTimer += deltaTime;
        List<string>? dropped = null;

        for (var i = _pendingMobs.Count - 1; i >= 0; i--)
        {
            var mob = _pendingMobs[i];
            var type = mob.Type == null ? null : EntityRegistry.TryGet(mob.Type);
            var known = type != null
                && ((_slimes != null && string.Equals(type.Name, "slime", StringComparison.OrdinalIgnoreCase))
                    || (_goblins != null && string.Equals(type.Name, "goblin", StringComparison.OrdinalIgnoreCase)));
            if (!known || mob.Position is not { Length: 3 } p)
            {
                (dropped ??= []).Add($"mob '{mob.Type ?? "?"}'");
                _pendingMobs.RemoveAt(i);
                continue;
            }

            var center = new Vector3(p[0], p[1], p[2]);
            if (!IsCellLoaded(center))
                continue;

            // A forced spawn, bounded by the hard cap rather than the natural one: a save only holds
            // mobs that lived together (splits may have taken them past the natural cap) plus entries
            // still pending, so restoring up to the hard cap reproduces the saved world, and the ring
            // spawners stay refused until the population falls under the natural cap again. At a full
            // hard cap the entry waits like an unloaded chunk: it stays pending (and is re-saved by
            // CaptureEntities) instead of overfilling the cap-sized healthbar buffers.
            var isSlime = string.Equals(type!.Name, "slime", StringComparison.OrdinalIgnoreCase);
            // A file saved before slimes had sizes carries none: those were all the smallest size
            Mob? spawned = isSlime
                ? _slimes!.Spawn(center, mob.Size ?? SlimeSize.Min)
                : _goblins!.Spawn(center, mob.Ammo ?? 0, mob.Torches ?? 0);
            if (spawned == null)
                continue;

            var velocity = ToVector(mob.Velocity);
            spawned.RestoreState(mob.Health ?? type.MaxHealth, mob.Facing ?? 0f, velocity);
            _pendingMobs.RemoveAt(i);
        }

        for (var i = _pendingDrops.Count - 1; i >= 0; i--)
        {
            var drop = _pendingDrops[i];
            if (!SaveNames.TryResolve(drop.Item, out var id) || drop.Position is not { Length: 3 } p)
            {
                (dropped ??= []).Add($"drop '{drop.Item ?? "?"}'");
                _pendingDrops.RemoveAt(i);
                continue;
            }

            var position = new Vector3(p[0], p[1], p[2]);
            if (!IsCellLoaded(position))
                continue;

            if (!_dropManager.SpawnRestored(id, position, ToVector(drop.Velocity), drop.Age ?? 0f))
                (dropped ??= []).Add($"drop '{drop.Item}'");
            _pendingDrops.RemoveAt(i);
        }

        if (dropped != null)
            Log.Warn($"Entity save: dropped {dropped.Count} unrestorable entr{(dropped.Count == 1 ? "y" : "ies")}: {string.Join(", ", dropped)}");

        if (!(_entityRestoreTimer >= WorldGenerationConfig.EntityRestoreTimeoutSeconds)
            || (_pendingMobs.Count <= 0 && _pendingDrops.Count <= 0)) return;

        Log.Warn($"Entity save: discarded {_pendingMobs.Count} mob(s) and {_pendingDrops.Count} drop(s) whose chunk never loaded or that the full mob hard cap kept out");
        _pendingMobs.Clear();
        _pendingDrops.Clear();
    }

    // Whether the chunk owning the cell under a world position is loaded
    private bool IsCellLoaded(Vector3 position) =>
        _chunkManager.ChunkAt(MathUtils.FloorToInt(position.X), MathUtils.FloorToInt(position.Y),
            MathUtils.FloorToInt(position.Z)) != null;

    // A saved float[3] as a vector; anything else is zero
    private static Vector3 ToVector(float[]? v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>
    /// One autosave pass: snapshots up to <see cref="WorldGenerationConfig.MaxAutosaveSnapshotsPerCall"/>
    /// modified loaded chunks to the save store (the copies are cheap; encoding and disk I/O run
    /// on its writer thread), then the far-LOD edit state if it changed, the player and the
    /// entities. A burst
    /// that fills the cap leaves the timer due, so the rest continues next frame instead of
    /// stalling this one.
    /// </summary>
    private void Autosave()
    {
        var snapshots = _chunkManager.FlushModified(WorldGenerationConfig.MaxAutosaveSnapshotsPerCall);
        if (snapshots >= WorldGenerationConfig.MaxAutosaveSnapshotsPerCall)
        {
            _autosaveTimer = WorldGenerationConfig.AutosaveIntervalSeconds;
            return;
        }

        _autosaveTimer = 0;
        SaveLodEditStateIfChanged();
        SavePlayerState();
        SaveEntityState();
    }

    /// <summary>
    /// The player as <see cref="PlayerSaveCodec"/> writes it. While still Loading the saved clock
    /// has not been applied yet, so a flush then keeps the saved one instead of overwriting it
    /// with an unrestored clock. No position is written before the spawn settled: until
    /// <c>EnsureSpawnSettled</c> has placed the player, <see cref="Camera.Position"/> is the
    /// pre-settle hold eye, and saving it would make <c>ApplySavedPlayer</c> mark the spawn settled
    /// on the next load and skip the surface search. A close mid-load therefore persists everything
    /// but the pose, and the next load searches again.
    /// </summary>
    private PlayerSaveData CapturePlayer()
    {
        if (_serverMode)
            return CaptureHostedPlayer();

        var pos = Camera.Position;
        return new PlayerSaveData
        {
            Position = LocalPlayer.SpawnSettled ? [pos.X, pos.Y, pos.Z] : null,
            Yaw = Camera.Yaw,
            Pitch = Camera.Pitch,
            Creative = Camera.IsCreative,
            Flying = Camera.IsFlying,
            Health = _health.Health,
            LightingClock = Phase == WorldPhase.Loading ? _savedPlayer?.LightingClock ?? LightingTime : LightingTime,
            RespawnEye = LocalPlayer.HasRespawnEye ? [LocalPlayer.RespawnEye.X, LocalPlayer.RespawnEye.Y, LocalPlayer.RespawnEye.Z] : null,
            Inventory = Inventory.Export()
        };
    }

    /// <summary>
    /// The player a server writes: never its own camera (forced creative and flying, parked at
    /// the players' centroid), but the player.json the world was opened with, with only the
    /// lighting clock refreshed. Its inventory too is written back as it was opened: every
    /// connected player carries their own (saved under <see cref="WorldStore.PlayersDirectoryName"/>),
    /// so nothing a client picks up or spends reaches the singleplayer owner's file. A world that
    /// had none gets no pose, no creative or flight, no respawn eye and no inventory, so opening it
    /// in singleplayer later searches the surface for a survival spawn as a new world would.
    /// </summary>
    private PlayerSaveData CaptureHostedPlayer() =>
        new()
        {
            Position = _hostedPlayer?.Position,
            Yaw = _hostedPlayer?.Yaw,
            Pitch = _hostedPlayer?.Pitch,
            Creative = _hostedPlayer?.Creative ?? false,
            Flying = _hostedPlayer?.Flying ?? false,
            Health = _hostedPlayer?.Health ?? _health.Health,
            // Like a game world: until the flip has applied the saved clock, keep the saved one
            LightingClock = Phase == WorldPhase.Loading ? _hostedPlayer?.LightingClock ?? LightingTime : LightingTime,
            RespawnEye = _hostedPlayer?.RespawnEye,
            Inventory = _hostedPlayer?.Inventory
        };

    /// <summary>Enqueues <see cref="PlayerSaveCodec.FileName"/>; does nothing without a save store.</summary>
    private void SavePlayerState()
    {
        _saveStore?.EnqueueFile(PlayerSaveCodec.FileName, PlayerSaveCodec.ToBytes(CapturePlayer()));
    }

    /// <summary>
    /// The live mobs and drops as <see cref="EntitySaveCodec"/> writes them: every living slime and
    /// goblin (kind by <see cref="EntityType.Name"/>, body center, health, facing, velocity, a
    /// goblin's ammo, a slime's size) and the item drops nearest the player first, capped at
    /// <see cref="WorldGenerationConfig.MaxSavedDrops"/>, by <see cref="SaveNames"/> name (an id
    /// with no name is skipped with one warning). Entries still pending a restore are appended, so a
    /// flush during loading, or before a far entity's chunk arrived, never loses them.
    /// </summary>
    private EntitySaveData CaptureEntities()
    {
        var slimes = Slimes;
        var mobs = (from t in slimes where !t.IsDead select MobSaveCodec.Capture(t, null, t.Size)).ToList();
        var goblins = Goblins;
        mobs.AddRange(from t in goblins where !t.IsDead select MobSaveCodec.Capture(t, t.Ammo, torches: t.Torches));
        mobs.AddRange(_pendingMobs);

        _dropSort.Clear();
        var eye = Camera.Position;
        var liveDrops = _dropManager.Drops;
        foreach (var itemDrop in liveDrops)
            _dropSort.Add((Vector3.DistanceSquared(itemDrop.Position, eye), itemDrop));

        _dropSort.Sort(static (a, b) => a.dist2.CompareTo(b.dist2));

        var drops = new List<EntitySaveData.DropData>();
        var unnamed = 0;
        for (var i = 0; i < _dropSort.Count && drops.Count < WorldGenerationConfig.MaxSavedDrops; i++)
        {
            var drop = _dropSort[i].drop;
            if (SaveNames.NameOf(drop.BlockId) is not { } name)
            {
                unnamed++;
                continue;
            }

            drops.Add(new EntitySaveData.DropData
            {
                Item = name,
                Position = [drop.Position.X, drop.Position.Y, drop.Position.Z],
                Velocity = [drop.Velocity.X, drop.Velocity.Y, drop.Velocity.Z],
                Age = drop.Age
            });
        }
        _dropSort.Clear();
        for (var i = 0; i < _pendingDrops.Count && drops.Count < WorldGenerationConfig.MaxSavedDrops; i++)
            drops.Add(_pendingDrops[i]);

        if (unnamed > 0)
            Log.Warn($"Entity save: skipped {unnamed} drop(s) of an item with no save name");
        return new EntitySaveData { Mobs = mobs, Drops = drops };
    }

    /// <summary>Enqueues <see cref="EntitySaveCodec.FileName"/>; does nothing without a save store.</summary>
    private void SaveEntityState()
    {
        _saveStore?.EnqueueFile(EntitySaveCodec.FileName, EntitySaveCodec.ToBytes(CaptureEntities()));
    }

    /// <summary>
    /// Enqueues <see cref="LodEditState.FileName"/> when the LOD edit state moved since the last
    /// write (<see cref="LodEditState.CombinedVersion"/>), or unconditionally when
    /// <paramref name="force"/> is set. Main thread only: <see cref="LodEditState.Capture"/>
    /// reads the process-global edit stores.
    /// </summary>
    private void SaveLodEditStateIfChanged(bool force = false)
    {
        if (_saveStore == null)
            return;
        var version = LodEditState.CombinedVersion;
        if (!force && version == _lodStateVersionSaved)
            return;
        _saveStore.EnqueueFile(LodEditState.FileName, LodEditState.Capture().ToBytes());
        _lodStateVersionSaved = version;
    }

    /// <summary>
    /// Hands every modified loaded chunk, the far-LOD edit state, the player
    /// (<see cref="PlayerSaveCodec.FileName"/>) and the mobs and drops
    /// (<see cref="EntitySaveCodec.FileName"/>) to the save store, so what the player sees now is
    /// what the save holds once the writer drains. Main thread only; no GL, no profiler span and no
    /// shutdown. <see cref="World.SaveAndDispose"/> is the caller that closes the world: it stops and
    /// joins the generation and LOD workers and drains the mesh runners
    /// (<see cref="ChunkManager.DrainMeshWork"/>) first, so this flush sees a quiescent world, then
    /// calls this, then <see cref="ChunkSaveStore.Shutdown"/> with
    /// <see cref="WorldGenerationConfig.SaveWriterShutdownTimeoutSeconds"/>, releases the geometry
    /// and finally <see cref="WorldSeed.EndWorld"/>. Does nothing without a save store.
    /// </summary>
    internal void FlushAll()
    {
        if (_saveStore == null)
            return;
        _chunkManager.FlushModified(int.MaxValue);
        SaveLodEditStateIfChanged(force: true);
        SavePlayerState();
        SaveEntityState();
        _autosaveTimer = 0;
    }
}
