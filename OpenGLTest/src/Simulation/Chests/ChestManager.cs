using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// Block-entity registry for chests, keyed by cell, main thread only like the other station managers. A chest is
/// created on demand the first time something uses it (<see cref="Open"/>) or breaks it: a ruin chest then rolls its
/// loot table from the hash of its cell and the world seed (<see cref="ChestLoot"/>), so a chest nobody touched holds
/// no state, is never saved and gives the same contents whichever way it is first used. A broken or blasted chest
/// spills what it holds as drops (<see cref="NotifyBlockRemoved"/>, the stale sweep in <see cref="Update"/>). Saved in
/// <c>stations.json</c> (<see cref="Capture"/>, <see cref="Restore"/>). Authoritative worlds only: a client holds no
/// contents of its own, only what the server sends the chest it has open.
/// </summary>
public sealed class ChestManager
{
    private readonly Dictionary<(int X, int Y, int Z), ChestState> _chests = new();
    private readonly List<(int X, int Y, int Z)> _stale = [];

    /// <summary>How many chests hold state.</summary>
    public int Count => _chests.Count;

    /// <summary>The state of a chest that has been used, or null when it never was.</summary>
    public ChestState? Peek(int x, int y, int z) => _chests.GetValueOrDefault((x, y, z));

    /// <summary>Whether the loaded cell holds a chest block, and its profile.</summary>
    public static bool TryProfileAt(ChunkManager chunks, int x, int y, int z, out ChestProfile profile) =>
        TryChestAt(chunks, x, y, z, out profile, out _);

    // The chest block's profile and its cell state (a tiered chest's tier) at a loaded cell
    private static bool TryChestAt(ChunkManager chunks, int x, int y, int z, out ChestProfile profile, out byte state)
    {
        profile = null!;
        if (!chunks.TryGetCell(x, y, z, out var id, out state) || id == 0) return false;
        if (BlockType.ById[id]?.Chest is not { } found) return false;
        profile = found;
        return true;
    }

    /// <summary>
    /// The state of the chest at a loaded cell, created (and, for a ruin or tower chest, rolled from its seed and, for a
    /// tiered chest, the tier in its cell state) when it was never used. Null when the cell is not a loaded chest block.
    /// </summary>
    public ChestState? Open(ChunkManager chunks, int x, int y, int z) =>
        TryChestAt(chunks, x, y, z, out var profile, out var cellState) ? GetOrCreate(x, y, z, profile, cellState) : null;

    private ChestState GetOrCreate(int x, int y, int z, ChestProfile profile, byte cellState)
    {
        if (_chests.TryGetValue((x, y, z), out var state)) return state;
        state = ChestLoot.Materialize(profile, cellState, x, y, z, WorldGenerationConfig.Seed);
        _chests[(x, y, z)] = state;
        return state;
    }

    /// <summary>
    /// A chest block was removed at the cell: what it held is dropped where it stood (a never-used ruin chest rolls
    /// its loot first, so breaking it unopened loses nothing) and the cell is forgotten. <paramref name="removed"/> is
    /// the broken block's chest profile and <paramref name="removedState"/> the broken cell's state, so a never-opened
    /// tiered chest rolls its own tier. <paramref name="drops"/> is null on a client, which holds nothing.
    /// </summary>
    public void NotifyBlockRemoved(int x, int y, int z, ChestProfile? removed, byte removedState, DropManager? drops)
    {
        var key = (x, y, z);
        if (!_chests.TryGetValue(key, out var state))
        {
            if (removed == null || drops == null) return;
            state = ChestLoot.Materialize(removed, removedState, x, y, z, WorldGenerationConfig.Seed);
        }
        else _chests.Remove(key);
        if (drops != null) Spill(state, x, y, z, drops);
    }

    /// <summary>
    /// Forgets the cells that stopped being chests without going through a break (a blast, a replacement), spilling
    /// what they held; a cell in an unloaded chunk freezes until it is back.
    /// </summary>
    public void Update(ChunkManager chunks, DropManager drops)
    {
        _stale.Clear();
        foreach (var (key, _) in _chests)
        {
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _)) continue;
            if (BlockType.ById[id]?.Chest == null) _stale.Add(key);
        }
        foreach (var key in _stale)
        {
            var state = _chests[key];
            _chests.Remove(key);
            Spill(state, key.X, key.Y, key.Z, drops);
        }
    }

    // Every unit as a drop at the cell, the way a quern hands its input back; a tool or piece of gear drops as its
    // plain item (a drop carries only an id), so its rolled modifiers and bound spells are lost with the chest
    private static void Spill(ChestState state, int x, int y, int z, DropManager drops)
    {
        var cell = new Vector3(x, y, z);
        for (var i = 0; i < state.SlotCount; i++)
        {
            var slot = state[i];
            for (var c = 0; c < slot.Count && !slot.IsEmpty; c++)
                drops.Spawn(slot.Id, cell);
        }
    }

    /// <summary>The chests that hold state, for <c>stations.json</c>.</summary>
    public List<StationSaveData.ChestData> Capture()
    {
        var list = new List<StationSaveData.ChestData>();
        foreach (var (key, state) in _chests)
        {
            if (list.Count >= ChestConfig.MaxChests) break;
            list.Add(ChestSaveCodec.Export(key.X, key.Y, key.Z, state));
        }
        return list;
    }

    /// <summary>Restores saved chests into the empty manager; returns how many entries were skipped (no cell).</summary>
    public int Restore(IReadOnlyList<StationSaveData.ChestData>? saved)
    {
        if (saved == null) return 0;
        var skipped = 0;
        var dropped = new List<string>();
        foreach (var data in saved)
        {
            if (data == null || !StationSaveCodec.TryCell(data.Cell, out var key) || ChestSaveCodec.Import(data, dropped) is not { } state)
            {
                skipped++;
                continue;
            }
            _chests[key] = state;
        }
        if (dropped.Count > 0)
            Log.Warn($"Chest save: dropped content this run cannot restore: {string.Join(", ", dropped)}");
        return skipped;
    }
}
