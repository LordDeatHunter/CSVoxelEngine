using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Leatherwork;

/// <summary>What a right-click on a vat did to the hand: spend the held item, and hand something over.</summary>
/// <param name="Consume">Whether one of the held item is spent.</param>
/// <param name="GiveId">What the hand gets, 0 for nothing (the emptied bucket, the leather, hides taken back).</param>
/// <param name="GiveCount">How many of <paramref name="GiveId"/>.</param>
public readonly record struct VatUse(bool Consume, ushort GiveId = 0, int GiveCount = 0);

/// <summary>
/// Block-entity registry for tanning vats (every block with a <see cref="BlockType.Tanning"/> profile), keyed by the
/// vat's cell. A vat is filled with a water bucket, takes tannin (bark) up to its capacity and a batch of its input
/// (salted hides) while nothing tans or waits in it, and tans the batch over time by <see cref="TanningRules"/>; an
/// empty hand (or any item the vat does not take) collects the result, or takes the batch back before it has started.
/// Main thread only; the authoritative world ticks it, saves it with the world and mirrors it to clients like the other
/// stations. An entry whose block is gone is swept and its contents dropped (the water is lost).
/// </summary>
public sealed class TanningVatManager
{
    /// <summary>Live state of one vat.</summary>
    public sealed class VatState
    {
        /// <summary>Block id of the vat this entry was registered with.</summary>
        public ushort BlockId;

        /// <summary>Whether it holds water.</summary>
        public bool Water;

        /// <summary>Tannin units in it.</summary>
        public int Bark;

        /// <summary>Inputs (salted hides) in the batch.</summary>
        public int Hides;

        /// <summary>Seconds the batch has tanned.</summary>
        public float Progress;

        /// <summary>Finished result (leather) waiting to be collected.</summary>
        public int Leather;

        /// <summary>The vat's profile, or null when its block is no vat.</summary>
        public TanningProfile? Profile => BlockType.ById[BlockId]?.Tanning;

        /// <summary>Whether nothing is in it.</summary>
        public bool IsEmpty => !Water && Bark == 0 && Hides == 0 && Leather == 0 && Progress == 0f;
    }

    private readonly Dictionary<(int X, int Y, int Z), VatState> _vats = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    /// <summary>Whether any vat is tracked.</summary>
    public bool HasAny => _vats.Count > 0;

    /// <summary>Registers a vat (or refreshes the block id of one already known).</summary>
    public void Ensure(Vector3 cell, ushort blockId)
    {
        var key = KeyOf(cell);
        if (!_vats.TryGetValue(key, out var state))
        {
            state = new VatState();
            _vats[key] = state;
        }
        state.BlockId = blockId;
    }

    /// <summary>Live state of the vat at this cell, or null when none is tracked.</summary>
    public VatState? StateAt(int x, int y, int z) => _vats.GetValueOrDefault((x, y, z));

    /// <summary>Every tracked vat, for the render pass.</summary>
    public IEnumerable<(Vector3 Cell, VatState State)> View()
    {
        foreach (var (key, state) in _vats)
            yield return (new Vector3(key.X, key.Y, key.Z), state);
    }

    /// <summary>
    /// One right-click on the vat with <paramref name="heldId"/> in hand (0 for an empty hand), whose use hands back
    /// <paramref name="heldReturnsId"/> (a water bucket's emptied bucket): water fills a dry vat, tannin adds a unit up to
    /// the capacity, an input joins the batch while nothing tans or waits and the batch has room, and anything else
    /// collects the waiting result, or takes back a batch that has not started. False when the click does nothing.
    /// </summary>
    public bool TryUse(Vector3 cell, ushort heldId, ushort heldReturnsId, out VatUse use)
    {
        use = default;
        if (!_vats.TryGetValue(KeyOf(cell), out var state) || state.Profile is not { } profile) return false;

        if (heldId != 0 && heldId == profile.WaterId)
        {
            if (state.Water) return false;
            state.Water = true;
            use = new VatUse(true, heldReturnsId, heldReturnsId == 0 ? 0 : 1);
            return true;
        }
        if (heldId != 0 && heldId == profile.TanninId)
        {
            if (state.Bark >= profile.Capacity) return false;
            state.Bark++;
            use = new VatUse(true);
            return true;
        }
        if (heldId != 0 && heldId == profile.InputId)
        {
            if (state.Leather > 0 || state.Progress > 0f || state.Hides >= profile.Capacity) return false;
            state.Hides++;
            use = new VatUse(true);
            return true;
        }

        if (state.Leather > 0)
        {
            use = new VatUse(false, profile.ResultId, state.Leather);
            state.Leather = 0;
            return true;
        }
        if (state.Hides == 0 || state.Progress > 0f) return false;
        use = new VatUse(false, profile.InputId, state.Hides);
        state.Hides = 0;
        return true;
    }

    /// <summary>
    /// Tans every vat for one frame (<see cref="TanningRules.Step"/>) and sweeps vats whose block is gone (broken,
    /// blasted or replaced): their batch, tannin and waiting result drop where they stood. A vat in an unloaded chunk
    /// is left alone.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _vats.Count == 0) return;
        _stale.Clear();
        foreach (var (key, state) in _vats)
        {
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _) || id != state.BlockId
                || state.Profile is not { } profile)
            {
                _stale.Add(key);
                continue;
            }
            TanningRules.Step(state, profile, deltaTime);
        }

        foreach (var key in _stale)
        {
            if (!_vats.Remove(key, out var state) || state.Profile is not { } profile) continue;
            var at = new Vector3(key.X, key.Y, key.Z);
            Drop(drops, profile.InputId, state.Hides, at);
            Drop(drops, profile.TanninId, state.Bark, at);
            Drop(drops, profile.ResultId, state.Leather, at);
        }
    }

    private static void Drop(DropManager drops, ushort id, int count, Vector3 at)
    {
        for (var i = 0; i < count; i++)
            drops.Spawn(id, at);
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 cell) => VoxelCoords.BlockCellOf(cell);

    /// <summary>Every tracked vat that holds anything, by content name, for <c>stations.json</c>.</summary>
    internal List<StationSaveData.VatData> Capture()
    {
        var result = new List<StationSaveData.VatData>();
        foreach (var ((x, y, z), state) in _vats)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (state.IsEmpty || SaveNames.NameOf(state.BlockId) is not { } block) continue;
            result.Add(new StationSaveData.VatData
            {
                Cell = [x, y, z],
                Block = block,
                Water = state.Water,
                Bark = state.Bark,
                Hides = state.Hides,
                Progress = state.Progress,
                Leather = state.Leather,
            });
        }
        return result;
    }

    /// <summary>Adds the vats saved by <see cref="Capture"/>, each count clamped to what its profile allows. An entry
    /// whose block no longer resolves to a vat, or whose numbers are out of range, is dropped; returns how many
    /// were.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.VatData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key) || !SaveNames.TryResolve(data.Block, out var block)
                || BlockType.ById[block]?.Tanning is not { } profile
                || data.Bark < 0 || data.Hides < 0 || data.Leather < 0 || !float.IsFinite(data.Progress))
            {
                skipped++;
                continue;
            }
            _vats[key] = new VatState
            {
                BlockId = block,
                Water = data.Water,
                Bark = Math.Min(data.Bark, profile.Capacity),
                Hides = Math.Min(data.Hides, profile.Capacity),
                Progress = Math.Clamp(data.Progress, 0f, profile.Seconds),
                Leather = Math.Min(data.Leather, profile.Capacity * profile.ResultPerInput),
            };
        }
        return skipped;
    }

    /// <summary>Writes every tracked vat, for the station broadcast.</summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_vats.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), state) in _vats)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.WriteId(state.BlockId);
            writer.Write((byte)(state.Water ? 1 : 0));
            writer.Write((byte)state.Bark);
            writer.Write((byte)state.Hides);
            writer.Write(state.Progress);
            writer.Write((byte)state.Leather);
        }
    }

    /// <summary>Replaces the tracked vats with a snapshot written by <see cref="WriteNet"/> (a client's mirror).</summary>
    internal void ReadNet(PacketReader reader)
    {
        _vats.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var state = new VatState
            {
                BlockId = BlockTables.RequireAssigned(reader.ReadId()),
                Water = reader.ReadByte() != 0,
                Bark = reader.ReadByte(),
                Hides = reader.ReadByte(),
                Progress = StationNetCodec.ReadFinite(reader),
                Leather = reader.ReadByte(),
            };
            if (state.Profile is not { } profile || state.Bark > profile.Capacity || state.Hides > profile.Capacity
                || state.Progress < 0f || state.Leather > profile.Capacity * profile.ResultPerInput)
                throw new System.IO.InvalidDataException("tanning vat snapshot out of range");
            _vats[key] = state;
        }
    }
}
