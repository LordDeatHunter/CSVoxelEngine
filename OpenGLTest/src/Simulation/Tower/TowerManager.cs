using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The battle towers' floor locks. Keeps one <see cref="TowerRecord"/> per tower (keyed by its placement cell), built
/// lazily the first time a target stands in its footprint with its chunk loaded. Each frame it recounts every floor's
/// living bound skeletons, releases the ones that strayed past <see cref="MobConfig.TowerSkeletonLeashBlocks"/> from
/// their tower, credits every despawned or released bound skeleton (and every timed-out restore) to its floor as owed,
/// lets the awake floors' piles raise their waves and owed skeletons (<see cref="SkeletonPileManager"/>), and opens a
/// floor's door once <see cref="TowerRules.DoorMayOpen"/> holds (the floor below open, every pile depleted, nothing
/// living, owed or pending a restore). A capped, non-blocking sweep then clears depleted piles and open doors through
/// <see cref="TowerBlockWriter"/>: a cell in an unloaded chunk waits without holding back the others. Each record also
/// carries its roof altar's <see cref="TowerAltarState"/>: <see cref="BeginRitual"/> starts the ritual once every door
/// is open, and <see cref="MarkDefeated"/> records the Skeleton King's death (<see cref="BossDefeated"/>). A defeated tower
/// then collapses into its ruin (BT-8): once <see cref="TowerRules.CollapseDue"/> holds, its <see cref="TowerCollapse"/>
/// rewrites it top-down after <see cref="MobConfig.TowerCollapseDelaySeconds"/>, sharing the writer's frame cap with the sweep
/// under <see cref="MobConfig.TowerCollapseScanCellsPerFrame"/>; unloaded cells wait per chunk, and the progress is saved. A
/// manager built without a writer is a client's display-only mirror and never ticks (the ruin reaches a client through the
/// cell journal, like the doors). Main thread only.
/// </summary>
public sealed class TowerManager(TowerBlockWriter? writer, Func<int, int, TowerSite?>? findSite = null)
{
    private readonly Func<int, int, TowerSite?> _findSite =
        findSite ?? ((x, z) => TowerPlacement.TryFindAt(x, z, out var site) ? site : null);
    private readonly Dictionary<(int, int), TowerRecord> _records = [];
    private readonly SkeletonPileManager _piles = new();
    // Bound skeletons owed back to their floors, gathered once a frame
    private readonly List<TowerFloorRef> _owedScratch = [];
    private float _scanTimer;
    // The frame's targets, whose bodies a collapse never fills with rubble
    private IReadOnlyList<IMobTarget> _targets = [];
    private Func<int, int, int, bool>? _bodyAt;

    /// <summary>The largest footprint radius a station snapshot may carry.</summary>
    private const int MaxNetRadius = 64;

    /// <summary>Whether any tower is tracked (so the station broadcast carries the section).</summary>
    public bool HasAny => _records.Count > 0;

    /// <summary>Every tracked tower.</summary>
    public IReadOnlyCollection<TowerRecord> Records => _records.Values;

    /// <summary>The record of <paramref name="site"/>, built from it on first use.</summary>
    internal TowerRecord Track(TowerSite site)
    {
        var key = (site.CellX, site.CellZ);
        if (_records.TryGetValue(key, out var record)) return record;
        record = TowerRecord.FromSite(site);
        _records[key] = record;
        return record;
    }

    /// <summary>Tracks a prebuilt record (replacing any under its key): the test and loopback seam.</summary>
    internal TowerRecord Track(TowerRecord record)
    {
        _records[record.Key] = record;
        return record;
    }

    /// <summary>The first tracked tower whose footprint disc holds column (<paramref name="x"/>, <paramref name="z"/>),
    /// or null.</summary>
    public TowerRecord? RecordAt(int x, int z)
    {
        foreach (var record in _records.Values)
            if (record.Contains(x, z)) return record;
        return null;
    }

    /// <summary>Owes one skeleton back to floor <paramref name="floor"/>, when that tower and floor are tracked.</summary>
    public void Owe(TowerFloorRef floor)
    {
        if (!_records.TryGetValue((floor.CellX, floor.CellZ), out var record)) return;
        if (floor.Floor < 0 || floor.Floor >= record.Floors.Length) return;
        record.Floors[floor.Floor].Owed++;
    }

    /// <summary>
    /// One frame of the locks: materializes the towers <paramref name="targets"/> stand in, recounts and leashes the
    /// bound skeletons, credits the owed ones (<paramref name="takeDiscarded"/> hands over timed-out restores), runs
    /// the awake floors' piles, judges every door (<paramref name="pending"/> counts a floor's skeletons still waiting
    /// to be restored), sweeps and runs the defeated towers' collapses. Does nothing on a client's mirror or without
    /// skeletons.
    /// </summary>
    public void Update(float dt, ChunkManager chunks, SkeletonManager? skeletons, IReadOnlyList<IMobTarget> targets,
        Func<TowerFloorRef, int>? pending, Action<List<TowerFloorRef>>? takeDiscarded)
    {
        if (writer == null || skeletons == null) return;
        writer.BeginFrame();
        ScanForTowers(dt, chunks, targets);
        RecountAndLeash(skeletons);
        CreditOwed(skeletons, takeDiscarded);
        JudgeFloors(dt, chunks, skeletons, targets, pending);
        Sweep(writer);
        _targets = targets;
        _bodyAt ??= BodyAt;
        Collapse(dt, chunks, writer);
    }

    /// <summary>
    /// Clears floor <paramref name="floor"/> of the tower at <paramref name="near"/> at once (the <c>towerclear</c>
    /// scenario op): its piles deplete, its owed skeletons are forgotten and its living bound skeletons die. False when
    /// no tower stands there, the floor does not exist or this is a client's mirror.
    /// </summary>
    public bool ClearFloor(Vector3 near, int floor, IReadOnlyList<Skeleton> skeletons)
    {
        if (writer == null) return false;
        var x = (int)MathF.Floor(near.X);
        var z = (int)MathF.Floor(near.Z);
        var record = RecordAt(x, z);
        if (record == null && _findSite(x, z) is { } site) record = Track(site);
        if (record == null || floor < 0 || floor >= record.Floors.Length) return false;

        var state = record.Floors[floor];
        foreach (var pile in state.Piles) pile.WavesLeft = 0;
        state.Owed = 0;
        for (var i = 0; i < skeletons.Count; i++)
        {
            var skeleton = skeletons[i];
            if (skeleton.IsDead || skeleton.IsDying || skeleton.Floor != state.Ref) continue;
            skeleton.TakeDamage(skeleton.Health);
        }
        return true;
    }

    /// <summary>Raised once when a tower's Skeleton King is killed (its altar turns
    /// <see cref="TowerAltarState.Defeated"/>). A public hook only: the collapse polls <see cref="TowerRules.CollapseDue"/>
    /// each frame instead, so a defeat restored from a save takes the same path as a live one.</summary>
    public event Action<TowerRecord>? BossDefeated;

    /// <summary>
    /// What a right-click on the altar cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) comes to,
    /// without changing any altar: <see cref="AltarVerdict.NoTower"/> when no tracked tower's altar stands there, else
    /// <see cref="TowerRules.AltarVerdictOf"/>. On the authoritative side (a manager with a writer) a tower not yet
    /// tracked is looked up from the world sites and may be tracked on this first use; a client's mirror judges only the
    /// towers its snapshot carries.
    /// </summary>
    public AltarVerdict JudgeAltar(int x, int y, int z)
    {
        var record = RecordAt(x, z);
        if (record == null && writer != null && _findSite(x, z) is { } site) record = Track(site);
        if (record == null || record.AltarCell != (x, y, z)) return AltarVerdict.NoTower;
        return TowerRules.AltarVerdictOf(record);
    }

    /// <summary>
    /// A right-click on the altar cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>): judges it like
    /// <see cref="JudgeAltar"/> and, on the authoritative side, starts the ritual when the verdict is
    /// <see cref="AltarVerdict.Begin"/>. Returns the verdict either way, so a repeated click reads
    /// <see cref="AltarVerdict.Stirring"/> and starts nothing twice.
    /// </summary>
    public AltarVerdict BeginRitual(int x, int y, int z)
    {
        var verdict = JudgeAltar(x, y, z);
        if (writer == null || verdict != AltarVerdict.Begin) return verdict;
        var record = RecordAt(x, z)!;
        record.AltarState = TowerAltarState.Ritual;
        record.RitualLeft = MobConfig.SkeletonKingRitualSeconds;
        return verdict;
    }

    /// <summary>Marks <paramref name="record"/>'s Skeleton King killed and raises <see cref="BossDefeated"/>, once: a
    /// record already defeated is left alone.</summary>
    internal void MarkDefeated(TowerRecord record)
    {
        if (record.AltarState == TowerAltarState.Defeated) return;
        record.AltarState = TowerAltarState.Defeated;
        record.RitualLeft = 0f;
        BossDefeated?.Invoke(record);
    }

    /// <summary>
    /// One frame of the altars: counts down every ritual whose king has not risen yet and, when it runs out, raises the
    /// king on top of the altar (<see cref="TowerRecord.KingHome"/>), turning the tower
    /// <see cref="TowerAltarState.Awake"/>; a spawn the hard cap refuses retries next frame. Then
    /// <see cref="SettleAltars"/>. Does nothing on a client's mirror or without kings.
    /// </summary>
    public void UpdateAltars(float dt, SkeletonKingManager? kings, Func<(int CellX, int CellZ), bool>? pendingKing)
    {
        if (writer == null || kings == null) return;
        foreach (var record in _records.Values)
        {
            if (record.AltarState != TowerAltarState.Ritual) continue;
            var status = kings.StatusFor(record.Key);
            if (status is not (KingStatus.None or KingStatus.Fading)) continue;
            record.RitualLeft -= dt;
            if (record.RitualLeft > 0f) continue;
            var home = record.KingHome;
            var king = kings.Spawn(home.Feet + new Vector3(0f, MobConfig.SkeletonKingHeight * 0.5f, 0f), home);
            if (king == null) continue;
            record.AltarState = TowerAltarState.Awake;
            record.RitualLeft = 0f;
        }
        SettleAltars(kings, pendingKing);
    }

    /// <summary>
    /// Reconciles every altar with its king (<see cref="SkeletonKingManager.StatusFor"/>): a killed king defeats the
    /// tower at once (<see cref="MarkDefeated"/>), even before its death has started; a living king under a ritual
    /// makes the tower <see cref="TowerAltarState.Awake"/>; and an awake tower whose king is gone or fading out (it left
    /// unkilled) turns <see cref="TowerAltarState.Dormant"/> again, so the ritual comes free, unless
    /// <paramref name="pendingKing"/> says its king is still waiting to be restored from a save. Runs each frame after
    /// the kings and right before the stations are saved, so a kill is never saved as dormant. Does nothing on a
    /// client's mirror or without kings.
    /// </summary>
    public void SettleAltars(SkeletonKingManager? kings, Func<(int CellX, int CellZ), bool>? pendingKing)
    {
        if (writer == null || kings == null) return;
        foreach (var record in _records.Values)
        {
            if (record.AltarState is not (TowerAltarState.Ritual or TowerAltarState.Awake)) continue;
            var status = kings.StatusFor(record.Key);
            if (status == KingStatus.Killed)
            {
                MarkDefeated(record);
                continue;
            }
            if (record.AltarState == TowerAltarState.Ritual)
            {
                if (status != KingStatus.Alive) continue;
                record.AltarState = TowerAltarState.Awake;
                record.RitualLeft = 0f;
                continue;
            }
            if (status == KingStatus.Alive || pendingKing?.Invoke(record.Key) == true) continue;
            record.AltarState = TowerAltarState.Dormant;
        }
    }

    /// <summary>Adds the top of every altar whose ritual is under way (where its sparks rise) to <paramref name="into"/>.
    /// Reads mirrors too.</summary>
    public void RitualAltars(List<Vector3> into)
    {
        foreach (var record in _records.Values)
        {
            if (record.AltarState != TowerAltarState.Ritual) continue;
            var (x, y, z) = record.AltarCell;
            into.Add(new Vector3(x + 0.5f, y + 1f, z + 0.5f));
        }
    }

    /// <summary>
    /// The look-at status line of the pile, door or altar block <paramref name="blockId"/> at (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="z"/>), or null for any other block or a cell outside every tracked tower.
    /// Reads mirrors too. A door is named by the floor whose band holds it (a door sits on its own floor's stair).
    /// </summary>
    public string? Detail(byte blockId, int x, int y, int z)
    {
        if (blockId != TowerPalette.PileBlock && blockId != TowerPalette.DoorBlock && blockId != TowerPalette.AltarBlock)
            return null;
        var record = RecordAt(x, z);
        if (record == null) return null;
        if (blockId == TowerPalette.AltarBlock) return TowerReadout.Altar(TowerRules.AltarVerdictOf(record));
        var k = record.FloorOf(y);
        var floor = record.Floors[k];
        if (blockId == TowerPalette.PileBlock) return TowerReadout.Pile(floor);
        if (floor.DoorOpen) return null;
        return TowerReadout.Door(floor, k == 0 || record.Floors[k - 1].DoorOpen);
    }

    /// <summary>The saved lock state of every tower built from a world site (<c>stations.json</c>), or null when there
    /// is none.</summary>
    public List<StationSaveData.TowerData>? Capture()
    {
        List<StationSaveData.TowerData>? towers = null;
        foreach (var record in _records.Values)
        {
            if (!record.HasSite) continue;
            var floors = new List<StationSaveData.TowerFloorData>(record.Floors.Length);
            foreach (var floor in record.Floors)
            {
                var waves = new int[floor.Piles.Length];
                for (var i = 0; i < waves.Length; i++) waves[i] = Math.Max(0, floor.Piles[i].WavesLeft);
                floors.Add(new StationSaveData.TowerFloorData { Waves = waves, DoorOpen = floor.DoorOpen, Owed = floor.Owed });
            }
            (towers ??= []).Add(new StationSaveData.TowerData
            {
                Cell = [record.Key.CellX, record.Key.CellZ],
                Floors = floors,
                Altar = record.AltarState == TowerAltarState.Dormant ? null : record.AltarState.ToString(),
                Collapse = record.Collapse?.Capture(),
            });
        }
        return towers;
    }

    /// <summary>
    /// Restores the towers <see cref="Capture"/> saved, rebuilding each record from its world site. An entry whose cell
    /// holds no tower, or whose floors, piles, waves or owed count no longer fit that tower, is skipped. An open door is
    /// re-swept (its cells already cleared read as gone) and a depleted pile's block is cleared by the sweep. Returns how
    /// many entries were skipped. The altar's state comes back by name (absent or unknown reads dormant), and a ritual
    /// under way restarts its timer. A defeated tower's saved collapse resumes at once (a finished one stays finished); a
    /// collapse entry on a tower that is not defeated is ignored, and a defeated tower with none starts a fresh collapse.
    /// </summary>
    public int Restore(List<StationSaveData.TowerData>? towers)
    {
        if (towers == null) return 0;
        var skipped = 0;
        foreach (var data in towers)
        {
            if (data?.Cell is not { Length: 2 } cell || data.Floors == null
                || !TowerPlacement.TryGetSite(cell[0], cell[1], out var site)
                || data.Floors.Count != site.Floors)
            {
                skipped++;
                continue;
            }
            var record = TowerRecord.FromSite(site);
            if (!TryApply(record, data.Floors))
            {
                skipped++;
                continue;
            }
            record.AltarState = Enum.TryParse<TowerAltarState>(data.Altar, true, out var altar) && Enum.IsDefined(altar)
                ? altar
                : TowerAltarState.Dormant;
            if (record.AltarState == TowerAltarState.Ritual) record.RitualLeft = MobConfig.SkeletonKingRitualSeconds;
            if (data.Collapse != null && record.AltarState == TowerAltarState.Defeated)
            {
                record.Collapse = TowerCollapse.ForSite(site, 0f);
                record.Collapse.Apply(data.Collapse);
            }
            _records[record.Key] = record;
        }
        return skipped;
    }

    // Applies one saved tower's floors onto a freshly built record; false when any floor does not fit it
    private static bool TryApply(TowerRecord record, List<StationSaveData.TowerFloorData> saved)
    {
        for (var k = 0; k < record.Floors.Length; k++)
        {
            var data = saved[k];
            var floor = record.Floors[k];
            if (data?.Waves is not { } waves || waves.Length != floor.Piles.Length) return false;
            if (data.Owed is < 0 or > MobConfig.MobHardCap) return false;
            foreach (var wave in waves)
                if (wave is < 0 or > MobConfig.SkeletonPileWaves) return false;
        }
        for (var k = 0; k < record.Floors.Length; k++)
        {
            var data = saved[k];
            var floor = record.Floors[k];
            for (var i = 0; i < floor.Piles.Length; i++) floor.Piles[i].WavesLeft = data.Waves![i];
            floor.Owed = data.Owed;
            floor.PilesLeft = floor.CountPilesLeft();
            if (!data.DoorOpen) continue;
            floor.DoorOpen = true;
            floor.DoorRemaining.AddRange(floor.DoorCells);
        }
        return true;
    }

    /// <summary>
    /// Writes every tracked tower to the station frame: its cell, center and radius, then per floor its band, piles left,
    /// skeletons left (both clamped to a byte) and door bit, then the altar's state byte, which is all a client's mirror
    /// shows.
    /// </summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_records.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var record in _records.Values)
        {
            if (left-- == 0) break;
            writer.Write(record.Key.CellX);
            writer.Write(record.Key.CellZ);
            writer.Write(record.CenterX);
            writer.Write(record.CenterZ);
            writer.Write(record.OuterRadius);
            writer.Write((byte)record.Floors.Length);
            foreach (var floor in record.Floors)
            {
                writer.Write(floor.BottomY);
                writer.Write(floor.TopY);
                writer.Write((byte)Math.Clamp(floor.PilesLeft, 0, byte.MaxValue));
                writer.Write((byte)Math.Clamp(floor.Alive, 0, byte.MaxValue));
                writer.Write(floor.DoorOpen);
            }
            writer.Write((byte)record.AltarState);
        }
    }

    /// <summary>Replaces the tracked towers with display-only mirrors of a snapshot written by <see cref="WriteNet"/>
    /// (a client's). A floor count outside 1..<see cref="MobConfig.TowerMaxTier"/> + 1, a radius outside
    /// 1..<see cref="MaxNetRadius"/>, an empty band or an undefined altar state is refused as malformed.</summary>
    internal void ReadNet(PacketReader reader)
    {
        _records.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = (reader.ReadInt(), reader.ReadInt());
            var centerX = reader.ReadInt();
            var centerZ = reader.ReadInt();
            var radius = reader.ReadInt();
            if (radius is < 1 or > MaxNetRadius) throw new InvalidDataException($"tower snapshot with radius {radius}");
            int floorCount = reader.ReadByte();
            if (floorCount is < 1 or > MobConfig.TowerMaxTier + 1)
                throw new InvalidDataException($"tower snapshot with {floorCount} floors");
            var floors = new (int Bottom, int Top, int PilesLeft, int Alive, bool DoorOpen)[floorCount];
            for (var k = 0; k < floorCount; k++)
            {
                var bottom = reader.ReadInt();
                var top = reader.ReadInt();
                if (top <= bottom) throw new InvalidDataException("tower snapshot with an empty floor band");
                floors[k] = (bottom, top, reader.ReadByte(), reader.ReadByte(), reader.ReadBool());
            }
            var altar = (TowerAltarState)reader.ReadByte();
            if (!Enum.IsDefined(altar)) throw new InvalidDataException($"tower snapshot with altar state {(byte)altar}");
            _records[key] = TowerRecord.Mirror(key, centerX, centerZ, radius, floors, altar);
        }
    }

    // Every TowerScanSeconds, tracks the tower each target with a loaded eye chunk stands in
    private void ScanForTowers(float dt, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        _scanTimer -= dt;
        if (_scanTimer > 0f) return;
        _scanTimer = MobConfig.TowerScanSeconds;
        for (var i = 0; i < targets.Count; i++)
        {
            var eye = targets[i].Eye;
            var x = (int)MathF.Floor(eye.X);
            var y = (int)MathF.Floor(eye.Y);
            var z = (int)MathF.Floor(eye.Z);
            if (chunks.ChunkAt(x, y, z) == null) continue;
            if (RecordAt(x, z) != null) continue;
            if (_findSite(x, z) is { } site) Track(site);
        }
    }

    // Zeroes every floor's living count, then counts each living bound skeleton, releasing those past the leash
    private void RecountAndLeash(SkeletonManager skeletons)
    {
        foreach (var record in _records.Values)
        foreach (var floor in record.Floors)
            floor.Living = 0;

        var mobs = skeletons.Mobs;
        for (var i = 0; i < mobs.Count; i++)
        {
            var skeleton = mobs[i];
            if (skeleton.IsDead || skeleton.IsDying || skeleton.Floor is not { } f) continue;
            if (!_records.TryGetValue((f.CellX, f.CellZ), out var record)) continue;
            if (f.Floor < 0 || f.Floor >= record.Floors.Length) continue;
            if (TowerRules.Strayed(skeleton.Position, record.CenterX, record.CenterZ, MobConfig.TowerSkeletonLeashBlocks))
            {
                skeletons.Release(skeleton);
                continue;
            }
            record.Floors[f.Floor].Living++;
        }
    }

    // Owes back every bound skeleton that despawned or was released, and every timed-out restore
    private void CreditOwed(SkeletonManager skeletons, Action<List<TowerFloorRef>>? takeDiscarded)
    {
        skeletons.TakeReleased(_owedScratch);
        takeDiscarded?.Invoke(_owedScratch);
        for (var i = 0; i < _owedScratch.Count; i++) Owe(_owedScratch[i]);
        _owedScratch.Clear();
    }

    // Runs the awake floors' piles (counting the skeletons they raise as living at once) and opens each door whose
    // floor is cleared, floor by floor so a door that opens wakes the floor above in the same frame
    private void JudgeFloors(float dt, ChunkManager chunks, SkeletonManager skeletons, IReadOnlyList<IMobTarget> targets,
        Func<TowerFloorRef, int>? pending)
    {
        foreach (var record in _records.Values)
        {
            var floors = record.Floors;
            for (var k = 0; k < floors.Length; k++)
            {
                var floor = floors[k];
                var previousOpen = k == 0 || floors[k - 1].DoorOpen;
                if (TowerRules.FloorAwake(k, previousOpen)) floor.Living += _piles.Update(floor, dt, chunks, skeletons, targets);
                floor.PilesLeft = floor.CountPilesLeft();
                floor.Alive = floor.Living + floor.Owed + (pending?.Invoke(floor.Ref) ?? 0);
                if (floor.DoorOpen || !TowerRules.DoorMayOpen(k, previousOpen, floor.PilesLeft, floor.Alive)) continue;
                floor.DoorOpen = true;
                floor.DoorRemaining.AddRange(floor.DoorCells);
            }
        }
    }

    // Runs every defeated tower's collapse under one scan budget and the writer's shared frame cap
    private void Collapse(float dt, ChunkManager chunks, TowerBlockWriter blocks)
    {
        var scan = MobConfig.TowerCollapseScanCellsPerFrame;
        foreach (var record in _records.Values)
        {
            if (record.Site is not { } site) continue;
            if (!TowerRules.CollapseDue(record.AltarState, record.Collapse?.Done == true)) continue;
            if (record.Collapse == null)
            {
                record.Collapse = TowerCollapse.ForSite(site, MobConfig.TowerCollapseDelaySeconds);
                Log.WriteLine($"Tower at ({record.CenterX}, {record.CenterZ}) begins to collapse.");
            }
            var going = record.Collapse.Step(dt, blocks, chunks, ref scan, _bodyAt);
            if (record.Collapse.Done) Log.WriteLine($"Tower at ({record.CenterX}, {record.CenterZ}) has collapsed into a ruin.");
            if (!going || scan <= 0) return;
        }
    }

    // Whether any target's body overlaps the cell (x, y, z)
    private bool BodyAt(int x, int y, int z)
    {
        var cell = new Vector3(x, y, z);
        for (var i = 0; i < _targets.Count; i++)
            if (PlayerContext.BodyOverlapsCell(_targets[i].Eye, cell)) return true;
        return false;
    }

    // Clears depleted piles and open doors, within the writer's frame budget; an unloaded cell waits for later
    private void Sweep(TowerBlockWriter blocks)
    {
        foreach (var record in _records.Values)
        foreach (var floor in record.Floors)
        {
            foreach (var pile in floor.Piles)
            {
                if (!pile.Depleted || pile.Removed) continue;
                var (x, y, z) = pile.Cell;
                var result = blocks.TryClear(x, y, z, TowerPalette.PileBlock);
                if (result == TowerWriteResult.Capped) return;
                if (result != TowerWriteResult.Unloaded) pile.Removed = true;
            }

            var door = floor.DoorRemaining;
            for (var i = door.Count - 1; i >= 0; i--)
            {
                var (x, y, z) = door[i];
                var result = blocks.TryClear(x, y, z, TowerPalette.DoorBlock);
                if (result == TowerWriteResult.Capped) return;
                if (result == TowerWriteResult.Unloaded) continue;
                door[i] = door[^1];
                door.RemoveAt(door.Count - 1);
            }
        }
    }
}
