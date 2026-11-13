using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Ingots;

/// <summary>
/// Block-entity registry for ingot piles (<see cref="BlockRenderer.IngotPile"/>): which ingots, in laying order, rest in
/// each pile cell. Mixed metals do not fit the cell's state byte, so the contents live here, keyed by cell, and
/// the chunk meshes nothing for the block: the pile is drawn as an entity pass (see <c>StationPasses.RenderIngotPiles</c>).
/// Authoritative state in singleplayer and on the server (a client's copy is replaced by the station snapshots). A
/// pile whose block is gone (broken, blasted, its support removed) spills its ingots as drops and is forgotten; an
/// unloaded cell freezes until its chunk is back. Main thread only.
/// </summary>
public sealed class IngotPileManager
{
    private readonly Dictionary<(int X, int Y, int Z), List<ushort>> _piles = new();
    private readonly List<(int X, int Y, int Z)> _stale = [];

    /// <summary>Whether any pile holds an ingot, for render early-out and the station broadcast.</summary>
    public bool HasAny => _piles.Count > 0;

    /// <summary>Whether the item is an ingot a pile takes.</summary>
    public static bool IsIngot(ushort itemId) =>
        itemId != 0 && ItemRegistry.TryGet(itemId) is { UseAction: ItemUseAction.PlaceIngot };

    /// <summary>How many ingots rest at the cell.</summary>
    public int CountAt(int x, int y, int z) => _piles.TryGetValue((x, y, z), out var pile) ? pile.Count : 0;

    /// <summary>The ingots at the cell in laying order (layer by layer, bottom first), or null when it holds none.</summary>
    public IReadOnlyList<ushort>? IngotsAt(int x, int y, int z) => _piles.GetValueOrDefault((x, y, z));

    /// <summary>
    /// Lays one ingot on the pile block at the cell, starting the pile when it holds none. False for a cell that is not
    /// a loaded pile block, an item that is no ingot and a full pile.
    /// </summary>
    public bool TryAdd(ChunkManager chunks, int x, int y, int z, ushort itemId)
    {
        if (!IsIngot(itemId) || !IsPileAt(chunks, x, y, z)) return false;
        if (!_piles.TryGetValue((x, y, z), out var pile))
            _piles[(x, y, z)] = pile = [];
        if (pile.Count >= IngotPileConfig.Capacity) return false;
        pile.Add(itemId);
        return true;
    }

    /// <summary>
    /// Takes the ingot laid last off the pile at the cell. Leaves the pile registered with no ingot when it was the
    /// only one (<see cref="IngotsAt"/> then reports nothing): the caller clears the block, which forgets the cell.
    /// False for a cell that holds none.
    /// </summary>
    public bool TryTakeLast(int x, int y, int z, out ushort itemId)
    {
        itemId = 0;
        if (!_piles.TryGetValue((x, y, z), out var pile) || pile.Count == 0) return false;
        itemId = pile[^1];
        pile.RemoveAt(pile.Count - 1);
        return true;
    }

    /// <summary>Drops every ingot of a broken pile where the block was and forgets the cell. A client passes null drops.</summary>
    public void NotifyBlockRemoved(int x, int y, int z, DropManager? drops)
    {
        if (!_piles.Remove((x, y, z), out var pile) || drops == null) return;
        Spill(pile, x, y, z, drops);
    }

    /// <summary>Spills and forgets the piles whose cell stopped being a pile block without a break (a blast, a lost support).</summary>
    public void Update(ChunkManager chunks, DropManager drops)
    {
        if (_piles.Count == 0) return;
        _stale.Clear();
        foreach (var key in _piles.Keys)
        {
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!IsPileAt(chunks, key.X, key.Y, key.Z)) _stale.Add(key);
        }
        foreach (var key in _stale)
            NotifyBlockRemoved(key.X, key.Y, key.Z, drops);
    }

    /// <summary>Every pile with its cell, for the render pass.</summary>
    public IEnumerable<(Vector3 Cell, IReadOnlyList<ushort> Ingots)> View()
    {
        foreach (var ((x, y, z), pile) in _piles)
            yield return (new Vector3(x, y, z), pile);
    }

    private static void Spill(List<ushort> pile, int x, int y, int z, DropManager drops)
    {
        var cell = new Vector3(x, y, z);
        foreach (var id in pile)
            drops.Spawn(id, cell);
    }

    private static bool IsPileAt(ChunkManager chunks, int x, int y, int z) =>
        chunks.TryGetCell(x, y, z, out var id, out _) && BlockType.ById[id]?.Renderer == BlockRenderer.IngotPile;

    /// <summary>Every pile by content name, for <c>stations.json</c> (at most <see cref="StationNetCodec.MaxEntries"/>).</summary>
    internal List<StationSaveData.IngotPileData> Capture() =>
        _piles.Take(StationNetCodec.MaxEntries).Select(entry => new StationSaveData.IngotPileData
        {
            Cell = [entry.Key.X, entry.Key.Y, entry.Key.Z],
            Ingots = entry.Value.Select(id => SaveNames.NameOf(id)).OfType<string>().ToList(),
        }).ToList();

    /// <summary>Adds the piles saved by <see cref="Capture"/> to an empty registry. An entry with a bad cell or no
    /// ingot that still resolves is dropped; returns how many were.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.IngotPileData>? saved)
    {
        if (saved == null) return 0;
        var skipped = 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key) || data.Ingots == null)
            {
                skipped++;
                continue;
            }
            var pile = new List<ushort>();
            foreach (var name in data.Ingots)
            {
                if (pile.Count >= IngotPileConfig.Capacity) break;
                if (SaveNames.TryResolve(name, out var id) && IsIngot(id)) pile.Add(id);
            }
            if (pile.Count == 0) skipped++;
            else _piles[key] = pile;
        }
        return skipped;
    }

    /// <summary>Writes every pile (cell, count, ingot ids) for the station broadcast.</summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_piles.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), pile) in _piles)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write((byte)pile.Count);
            foreach (var id in pile)
                writer.WriteId(id);
        }
    }

    /// <summary>Replaces the piles with a snapshot written by <see cref="WriteNet"/>: a client's display-only mirror.</summary>
    internal void ReadNet(PacketReader reader)
    {
        var count = StationNetCodec.ReadCount(reader);
        var read = new Dictionary<(int X, int Y, int Z), List<ushort>>(count);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var n = reader.ReadByte();
            if (n is 0 or > IngotPileConfig.Capacity)
                throw new InvalidDataException($"ingot pile with {n} ingots");
            var pile = new List<ushort>(n);
            for (var k = 0; k < n; k++)
            {
                var id = BlockTables.RequireAssigned(reader.ReadId());
                if (!IsIngot(id)) throw new InvalidDataException($"ingot pile holds item {id}, which is no ingot");
                pile.Add(id);
            }
            read[key] = pile;
        }
        _piles.Clear();
        foreach (var (key, pile) in read)
            _piles[key] = pile;
    }
}
