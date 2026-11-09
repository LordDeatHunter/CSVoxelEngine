using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Networking;
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
/// <see cref="TowerBlockWriter"/>: a cell in an unloaded chunk waits without holding back the others. A manager built
/// without a writer is a client's display-only mirror and never ticks. Main thread only.
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
    /// to be restored) and sweeps. Does nothing on a client's mirror or without skeletons.
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

    /// <summary>
    /// The look-at status line of the pile or door block <paramref name="blockId"/> at (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="z"/>), or null for any other block or a cell outside every tracked tower.
    /// Reads mirrors too. A door is named by the floor whose band holds it (a door sits on its own floor's stair).
    /// </summary>
    public string? Detail(byte blockId, int x, int y, int z)
    {
        if (blockId != TowerPalette.PileBlock && blockId != TowerPalette.DoorBlock) return null;
        var record = RecordAt(x, z);
        if (record == null) return null;
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
            (towers ??= []).Add(new StationSaveData.TowerData { Cell = [record.Key.CellX, record.Key.CellZ], Floors = floors });
        }
        return towers;
    }

    /// <summary>
    /// Restores the towers <see cref="Capture"/> saved, rebuilding each record from its world site. An entry whose cell
    /// holds no tower, or whose floors, piles, waves or owed count no longer fit that tower, is skipped. An open door is
    /// re-swept (its cells already cleared read as gone) and a depleted pile's block is cleared by the sweep. Returns how
    /// many entries were skipped.
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
    /// skeletons left (both clamped to a byte) and door bit, which is all a client's mirror shows.
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
        }
    }

    /// <summary>Replaces the tracked towers with display-only mirrors of a snapshot written by <see cref="WriteNet"/>
    /// (a client's). A floor count outside 1..<see cref="MobConfig.TowerMaxTier"/> + 1, a radius outside
    /// 1..<see cref="MaxNetRadius"/> or an empty band is refused as malformed.</summary>
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
            _records[key] = TowerRecord.Mirror(key, centerX, centerZ, radius, floors);
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
