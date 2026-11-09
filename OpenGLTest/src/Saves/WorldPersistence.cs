using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Icebergs;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Structures;

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
    private readonly HellMobManager? _hellMobs;
    private readonly SkyMobManager? _skyMobs;
    private readonly SkeletonManager? _skeletons;
    private readonly SkeletonKingManager? _kings;
    private readonly SandwormManager? _sandworms;
    private readonly IcebergManager? _icebergs;
    private readonly PlayerHealth _health;
    private readonly bool _serverMode;
    private readonly CampfireManager _campfires;
    private readonly QuernManager _querns;
    private readonly KilnManager _kilns;
    private readonly CastingManager _casting;

    private WorldPhase Phase => _world.Phase;
    private double LightingTime => _world.LightingTime;
    private Camera Camera => _world.Camera;
    private Inventory Inventory => _world.Inventory;
    private IReadOnlyList<Slime> Slimes => _world.Slimes;
    private IReadOnlyList<Goblin> Goblins => _world.Goblins;
    private IReadOnlyList<HellMob> HellMobs => _world.HellMobs;
    private IReadOnlyList<SkyMob> SkyMobs => _world.SkyMobs;
    private IReadOnlyList<Skeleton> Skeletons => _world.Skeletons;
    private IReadOnlyList<SkeletonKing> SkeletonKings => _world.SkeletonKings;
    private IReadOnlyList<Sandworm> Sandworms => _world.Sandworms;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;

    internal WorldPersistence(World world, ChunkSaveStore? saveStore, ChunkManager chunkManager,
        DropManager dropManager, SlimeManager? slimes, GoblinManager? goblins, HellMobManager? hellMobs,
        SkyMobManager? skyMobs, SkeletonManager? skeletons, SkeletonKingManager? kings, SandwormManager? sandworms, IcebergManager? icebergs,
        PlayerHealth health, bool serverMode, PlayerSaveData? savedPlayer, EntitySaveData? savedEntities,
        CampfireManager campfires, QuernManager querns, KilnManager kilns, CastingManager casting,
        StationSaveData? savedStations)
    {
        _world = world;
        _campfires = campfires;
        _querns = querns;
        _kilns = kilns;
        _casting = casting;
        RestoreStations(savedStations);
        _saveStore = saveStore;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _slimes = slimes;
        _goblins = goblins;
        _hellMobs = hellMobs;
        _skyMobs = skyMobs;
        _skeletons = skeletons;
        _kings = kings;
        _sandworms = sandworms;
        _icebergs = icebergs;
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
        if (savedEntities?.Icebergs != null)
            _pendingIcebergs.AddRange(savedEntities.Icebergs);
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
            if (_autosaveTimer >= SaveConfig.AutosaveIntervalSeconds)
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
    private readonly List<EntitySaveData.IcebergData> _pendingIcebergs = [];
    private double _entityRestoreTimer;

    // The tower floors of saved skeletons a restore timeout discarded, owed back to their floors
    // (TakeDiscardedTowerSkeletons), so a reload never opens a door early
    private readonly List<TowerFloorRef> _discardedTowerSkeletons = [];

    // Scratch for CaptureEntities' nearest-first drop order, reused across saves
    private readonly List<(float dist2, ItemDrop drop)> _dropSort = [];

    // Seconds since the last completed autosave pass
    private double _autosaveTimer;

    // LodEditState.CombinedVersion when lod-edits.bin was last enqueued; -1 so the first
    // autosave always writes it
    private int _lodStateVersionSaved = -1;

    // WoodDamage.Version when wood-damage.bin was last enqueued; 0 is a store nothing ever changed, which writes nothing
    private int _woodDamageVersionSaved;

    /// <summary>
    /// Spawns every pending saved mob and drop whose chunk is loaded, through the managers' own
    /// spawn paths, and restores its health, facing and motion. Each waits for its chunk because
    /// an entity restored over an unloaded chunk would fall through air (slimes, goblins and drops
    /// all read unloaded cells as air). A restore is a forced spawn: it may pass the natural mob cap
    /// but a mob waits the same way while the hard cap (<see cref="MobConfig.MobHardCap"/>,
    /// which sizes the healthbar buffers) is full, so an over-full save never overfills the world
    /// and loses nothing, since pending entries are re-saved. An unknown entity kind or item name, or a record with no
    /// position, is dropped with one warning per pass. After
    /// <see cref="SaveConfig.EntityRestoreTimeoutSeconds"/> of play whatever is still
    /// pending is discarded. Main thread, Playing only; nothing pending costs one check.
    /// </summary>
    internal void RestorePendingEntities(double deltaTime)
    {
        if (_pendingMobs.Count == 0 && _pendingDrops.Count == 0 && _pendingIcebergs.Count == 0)
            return;
        _entityRestoreTimer += deltaTime;
        List<string>? dropped = null;

        for (var i = _pendingMobs.Count - 1; i >= 0; i--)
        {
            var mob = _pendingMobs[i];
            var type = mob.Type == null ? null : EntityRegistry.TryGet(mob.Type);
            if (type == null || !KnowsKind(type.Name) || mob.Position is not { Length: 3 } p)
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
            var spawned = SpawnSaved(type.Name, center, mob);
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

        RestorePendingIcebergs(ref dropped);

        if (dropped != null)
            Log.Warn($"Entity save: dropped {dropped.Count} unrestorable entr{(dropped.Count == 1 ? "y" : "ies")}: {string.Join(", ", dropped)}");

        if (!(_entityRestoreTimer >= SaveConfig.EntityRestoreTimeoutSeconds)
            || (_pendingMobs.Count <= 0 && _pendingDrops.Count <= 0 && _pendingIcebergs.Count <= 0)) return;

        Log.Warn($"Entity save: discarded {_pendingMobs.Count} mob(s), {_pendingDrops.Count} drop(s) and {_pendingIcebergs.Count} iceberg(s) whose chunks never loaded or that a full cap kept out");
        foreach (var mob in _pendingMobs)
            if (TowerFloorOf(mob) is { } floor)
                _discardedTowerSkeletons.Add(floor);
        _pendingMobs.Clear();
        _pendingDrops.Clear();
        _pendingIcebergs.Clear();
    }

    /// <summary>How many saved skeletons bound to <paramref name="floor"/> still wait for a restore; they count
    /// as alive toward the floor's lock.</summary>
    internal int PendingTowerSkeletons(TowerFloorRef floor)
    {
        var count = 0;
        foreach (var mob in _pendingMobs)
            if (TowerFloorOf(mob) == floor)
                count++;
        return count;
    }

    /// <summary>Appends the tower floors of the saved skeletons a restore timeout discarded to
    /// <paramref name="into"/>, then forgets them: each is owed back to its floor.</summary>
    internal void TakeDiscardedTowerSkeletons(List<TowerFloorRef> into)
    {
        into.AddRange(_discardedTowerSkeletons);
        _discardedTowerSkeletons.Clear();
    }

    /// <summary>Whether a saved Skeleton King bound to tower <paramref name="tower"/> still waits for a restore, so
    /// its tower stays awake rather than freeing the ritual.</summary>
    internal bool PendingKingFor((int CellX, int CellZ) tower)
    {
        foreach (var mob in _pendingMobs)
            if (mob.Type != null && SkeletonKingManager.HeightOf(mob.Type) > 0f
                && mob.TowerCell is { Length: 2 } cell && cell[0] == tower.CellX && cell[1] == tower.CellZ)
                return true;
        return false;
    }

    // The tower floor a saved skeleton counts toward, or null for an untagged or other entry
    private static TowerFloorRef? TowerFloorOf(EntitySaveData.MobData mob) =>
        mob.TowerCell is { Length: 2 } cell && mob.TowerFloor is >= 0 and var floor
        && mob.Type != null && SkeletonManager.HeightOf(mob.Type) > 0f
            ? new TowerFloorRef(cell[0], cell[1], floor)
            : null;

    // Spawns every pending saved iceberg once every chunk under its placed bounds is loaded (a berg
    // collides with its whole footprint, so a part over an unloaded chunk would refuse it). A spawn
    // the cap or a blocked footprint refuses stays pending.
    private void RestorePendingIcebergs(ref List<string>? dropped)
    {
        for (var i = _pendingIcebergs.Count - 1; i >= 0; i--)
        {
            var saved = _pendingIcebergs[i];
            if (_icebergs == null || saved.Position is not { Length: 3 } p)
            {
                (dropped ??= []).Add("iceberg");
                _pendingIcebergs.RemoveAt(i);
                continue;
            }

            var variant = Math.Clamp(saved.Variant ?? 0, 0, _icebergs.VariantCount - 1);
            var scale = Math.Clamp(saved.Scale ?? 1f, IcebergConfig.ScaleMin, IcebergConfig.ScaleMax);
            var turns = (saved.Turns ?? 0) & 3;
            var center = new Vector3(p[0], p[1], p[2]);
            var (min, max) = _icebergs.ShapeOf(variant).PlacedBounds(turns, scale, center);
            if (!IcebergManager.ChunksLoaded(_chunkManager, min, max))
                continue;

            var berg = _icebergs.Spawn(_chunkManager, variant, scale, turns, saved.Seed ?? 0, center,
                saved.Life ?? IcebergConfig.LifeMaxSeconds);
            if (berg == null)
                continue;
            berg.Velocity = ToVector(saved.Velocity);
            _pendingIcebergs.RemoveAt(i);
        }
    }

    // Whether the chunk owning the cell under a world position is loaded
    private bool IsCellLoaded(Vector3 position) =>
        _chunkManager.ChunkAt(MathUtils.FloorToInt(position.X), MathUtils.FloorToInt(position.Y),
            MathUtils.FloorToInt(position.Z)) != null;

    // A saved float[3] as a vector; anything else is zero
    private static Vector3 ToVector(float[]? v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>
    /// One autosave pass: snapshots up to <see cref="SaveConfig.MaxAutosaveSnapshotsPerCall"/>
    /// modified loaded chunks to the save store (the copies are cheap; encoding and disk I/O run
    /// on its writer thread), then the far-LOD edit state if it changed, the player and the
    /// entities. A burst
    /// that fills the cap leaves the timer due, so the rest continues next frame instead of
    /// stalling this one.
    /// </summary>
    private void Autosave()
    {
        var snapshots = _chunkManager.FlushModified(SaveConfig.MaxAutosaveSnapshotsPerCall);
        if (snapshots >= SaveConfig.MaxAutosaveSnapshotsPerCall)
        {
            _autosaveTimer = SaveConfig.AutosaveIntervalSeconds;
            return;
        }

        _autosaveTimer = 0;
        SaveLodEditStateIfChanged();
        SaveWoodDamageIfChanged();
        SavePlayerState();
        SaveEntityState();
        SaveStationState();
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
            Mana = _health.Mana.Current,
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
            Mana = _hostedPlayer?.Mana ?? _health.Mana.Current,
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

    // Whether a saved mob of the named kind can come back: its kind is one this world runs
    private bool KnowsKind(string name) =>
        (_slimes != null && string.Equals(name, "slime", StringComparison.OrdinalIgnoreCase))
        || (_goblins != null && string.Equals(name, "goblin", StringComparison.OrdinalIgnoreCase))
        || (_hellMobs != null && HellMobManager.HeightOf(name) > 0f)
        || (_skyMobs != null && SkyMobManager.HeightOf(name) > 0f)
        || (_skeletons != null && SkeletonManager.HeightOf(name) > 0f)
        || (_kings != null && SkeletonKingManager.HeightOf(name) > 0f)
        || (_sandworms != null && string.Equals(name, Sandworm.EntityName, StringComparison.OrdinalIgnoreCase));

    // A forced spawn of a saved mob of a known kind (null at a full hard cap); a hell mob's layer and a sky
    // mob's zone tier, and so its pool, follow from its saved position, while a skeleton's tower tier is saved
    private Mob? SpawnSaved(string name, Vector3 center, EntitySaveData.MobData mob)
    {
        // Before the skeleton and hell fall-throughs: a Skeleton King comes back bound to its tower (its home, leash
        // and summon tier follow from the site), with no ritual and no rise; the restored health then sets the
        // brain's phase silently on its first observation, so a reload never re-summons
        if (SkeletonKingManager.HeightOf(name) > 0f)
        {
            var home = mob.TowerCell is { Length: 2 } c && TowerPlacement.TryGetSite(c[0], c[1], out var site)
                ? _world.Towers.Track(site).KingHome
                : SkeletonKingHome.Unbound(center - new Vector3(0f, MobConfig.SkeletonKingHeight / 2f, 0f));
            return _kings!.Spawn(center, home, spawnIn: false);
        }
        // A file saved before slimes had sizes carries none: those were all the smallest size
        if (string.Equals(name, "slime", StringComparison.OrdinalIgnoreCase))
            return _slimes!.Spawn(center, mob.Size ?? SlimeSize.Min);
        if (string.Equals(name, "goblin", StringComparison.OrdinalIgnoreCase))
            return _goblins!.Spawn(center, mob.Ammo ?? 0, mob.Torches ?? 0);
        if (SkyMobManager.HeightOf(name) > 0f)
            return _skyMobs!.Spawn(name, center);
        // Before the hell fall-through, which would otherwise take the skeleton
        if (SkeletonManager.HeightOf(name) > 0f)
        {
            var skeleton = _skeletons!.Spawn(center, mob.Tier ?? 0);
            if (skeleton != null) skeleton.Floor = TowerFloorOf(mob);
            return skeleton;
        }
        if (string.Equals(name, Sandworm.EntityName, StringComparison.OrdinalIgnoreCase))
        {
            // A restored worm resumes the hunt under the sand at once, without a second thump
            var worm = _sandworms!.Spawn(center, mob.Facing ?? 0f);
            worm?.Brain.SkipThump();
            return worm;
        }
        return _hellMobs!.Spawn(name, center);
    }

    /// <summary>
    /// The live mobs and drops as <see cref="EntitySaveCodec"/> writes them: every living slime,
    /// goblin, hell and sky mob, skeleton, Skeleton King (not one fading out) and sandworm (kind by <see cref="EntityType.Name"/>, body center, health,
    /// facing, velocity, a goblin's ammo and torches, a slime's size, a skeleton's tower tier, a king's tower and summon tier) and the item drops nearest the player first, capped at
    /// <see cref="SaveConfig.MaxSavedDrops"/>, by <see cref="SaveNames"/> name (an id
    /// with no name is skipped with one warning). Entries still pending a restore are appended, so a
    /// flush during loading, or before a far entity's chunk arrived, never loses them.
    /// </summary>
    private EntitySaveData CaptureEntities()
    {
        var slimes = Slimes;
        var mobs = (from t in slimes where !t.IsDead select MobSaveCodec.Capture(t, null, t.Size)).ToList();
        var goblins = Goblins;
        mobs.AddRange(from t in goblins where !t.IsDead select MobSaveCodec.Capture(t, t.Ammo, torches: t.Torches));
        mobs.AddRange(from t in HellMobs where !t.IsDead select MobSaveCodec.Capture(t, null));
        mobs.AddRange(from t in SkyMobs where !t.IsDead select MobSaveCodec.Capture(t, null));
        mobs.AddRange(from t in Skeletons where !t.IsDead select MobSaveCodec.Capture(t, null, tier: t.Tier, floor: t.Floor));
        // A fading king is not saved: a reload never brings back a king its tower already let go
        mobs.AddRange(from t in SkeletonKings where !t.IsDead && !t.IsDying
            select MobSaveCodec.Capture(t, null, tier: t.Home.SummonTier, tower: t.Home.Tower));
        mobs.AddRange(from t in Sandworms where !t.IsDead select MobSaveCodec.Capture(t, null));
        mobs.AddRange(_pendingMobs);

        _dropSort.Clear();
        var eye = Camera.Position;
        var liveDrops = _dropManager.Drops;
        foreach (var itemDrop in liveDrops)
            _dropSort.Add((Vector3.DistanceSquared(itemDrop.Position, eye), itemDrop));

        _dropSort.Sort(static (a, b) => a.dist2.CompareTo(b.dist2));

        var drops = new List<EntitySaveData.DropData>();
        var unnamed = 0;
        for (var i = 0; i < _dropSort.Count && drops.Count < SaveConfig.MaxSavedDrops; i++)
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
        for (var i = 0; i < _pendingDrops.Count && drops.Count < SaveConfig.MaxSavedDrops; i++)
            drops.Add(_pendingDrops[i]);

        if (unnamed > 0)
            Log.Warn($"Entity save: skipped {unnamed} drop(s) of an item with no save name");
        return new EntitySaveData { Mobs = mobs, Drops = drops, Icebergs = CaptureIcebergs() };
    }

    // Every live iceberg that is not sinking away, plus the saved ones still pending a restore
    private List<EntitySaveData.IcebergData> CaptureIcebergs()
    {
        var icebergs = new List<EntitySaveData.IcebergData>();
        foreach (var berg in _world.Icebergs)
        {
            if (berg.IsSinking)
                continue;
            icebergs.Add(new EntitySaveData.IcebergData
            {
                Variant = berg.Variant,
                Scale = berg.Scale,
                Turns = berg.Turns,
                Seed = berg.Seed,
                Position = [berg.Center.X, berg.Center.Y, berg.Center.Z],
                Velocity = [berg.Velocity.X, berg.Velocity.Y, berg.Velocity.Z],
                Life = berg.LifeLeft,
            });
        }
        icebergs.AddRange(_pendingIcebergs);
        return icebergs;
    }

    /// <summary>Enqueues <see cref="EntitySaveCodec.FileName"/>; does nothing without a save store.</summary>
    private void SaveEntityState()
    {
        _saveStore?.EnqueueFile(EntitySaveCodec.FileName, EntitySaveCodec.ToBytes(CaptureEntities()));
    }

    /// <summary>
    /// Fills the (empty) station registries from <c>stations.json</c>. No wait for chunks is needed:
    /// each manager freezes a cell in an unloaded chunk and, once the chunk is there, its own update
    /// sweeps an entry whose block is gone (dropping what it held), so a stale entry never lives on.
    /// One warning covers every entry dropped for a name that no longer resolves.
    /// </summary>
    private void RestoreStations(StationSaveData? saved)
    {
        if (saved == null)
            return;
        var skipped = _campfires.Restore(saved.Campfires) + _querns.Restore(saved.Querns)
            + _kilns.Restore(saved.Kilns) + _casting.Restore(saved.Casts) + _world.Chests.Restore(saved.Chests)
            + _world.Waystones.Restore(saved.Waystones) + _world.Smithing.Restore(saved)
            + _world.Towers.Restore(saved.Towers);
        if (skipped > 0)
            Log.Warn($"Station save: dropped {skipped} saved station entr{(skipped == 1 ? "y" : "ies")} that no longer resolve");
    }

    /// <summary>Enqueues <see cref="StationSaveCodec.FileName"/>; does nothing without a save store.</summary>
    private void SaveStationState()
    {
        if (_saveStore == null) return;
        // A king killed earlier this frame defeats its tower before the altar is captured, never saved as dormant
        _world.Towers.SettleAltars(_kings, PendingKingFor);
        var data = new StationSaveData
        {
            Campfires = _campfires.Capture(),
            Querns = _querns.Capture(),
            Kilns = _kilns.Capture(),
            Casts = _casting.Capture(),
            Chests = _world.Chests.Capture(),
            Waystones = _world.Waystones.Capture(),
            Towers = _world.Towers.Capture(),
        };
        _world.Smithing.Capture(data);
        _saveStore.EnqueueFile(StationSaveCodec.FileName, StationSaveCodec.ToBytes(data));
    }

    /// <summary>Enqueues <see cref="WoodDamageCodec.FileName"/> when the chop damage changed since the last
    /// write; does nothing without a save store.</summary>
    private void SaveWoodDamageIfChanged()
    {
        var damage = _chunkManager.ChopDamage;
        var version = damage.Version;
        if (_saveStore == null || version == _woodDamageVersionSaved)
            return;
        _saveStore.EnqueueFile(WoodDamageCodec.FileName, WoodDamageCodec.ToBytes(damage.Export()));
        _woodDamageVersionSaved = version;
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
    /// <see cref="SaveConfig.SaveWriterShutdownTimeoutSeconds"/>, releases the geometry
    /// and finally <see cref="WorldSeed.EndWorld"/>. Does nothing without a save store.
    /// </summary>
    internal void FlushAll()
    {
        if (_saveStore == null)
            return;
        _chunkManager.FlushModified(int.MaxValue);
        SaveLodEditStateIfChanged(force: true);
        SaveWoodDamageIfChanged();
        SavePlayerState();
        SaveEntityState();
        SaveStationState();
        _autosaveTimer = 0;
    }
}
