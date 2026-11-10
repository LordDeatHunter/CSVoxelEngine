using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Player;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// The world's waystone registry, keyed by cell, main thread only like the station managers. A record is created the
/// first time a player activates a waystone (<see cref="Discover"/>), which names it at random
/// (<see cref="WaystoneNames.Generate"/>) and remembers that player as its first discoverer, the only one who may
/// rename it (<see cref="TryRename"/>); an untouched waystone holds no record and is never saved. A broken one is kept,
/// marked lost (<see cref="NotifyBlockRemoved"/> from a break, the sweep in <see cref="Update"/> for anything else), so
/// it stays in its discoverers' lists. Saved in <c>stations.json</c> (<see cref="Capture"/>, <see cref="Restore"/>).
/// Authoritative worlds only: a client holds no records of its own.
/// </summary>
public sealed class WaystoneManager(Random? rng = null)
{
    /// <summary>The discoverer id of the local (singleplayer) player. Never a player name slug, which holds only
    /// letters, digits and dashes.</summary>
    public const string LocalDiscoverer = "@local";

    private readonly Random _rng = rng ?? Random.Shared;
    private readonly Dictionary<(int X, int Y, int Z), WaystoneRecord> _records = new();

    /// <summary>How many waystones hold a record.</summary>
    public int Count => _records.Count;

    /// <summary>Moves whenever a record is created, renamed or lost (what a client sync or an open list reads).</summary>
    public int Version { get; private set; }

    /// <summary>Every record by cell.</summary>
    public IReadOnlyDictionary<(int X, int Y, int Z), WaystoneRecord> Records => _records;

    /// <summary>The record of a waystone that has been activated, or null when it never was.</summary>
    public WaystoneRecord? Peek(int x, int y, int z) => _records.GetValueOrDefault((x, y, z));

    /// <summary>Who <paramref name="player"/> is to the registry: <see cref="LocalDiscoverer"/> for the local player
    /// (a context with no name), else the player's name slug, the key of its <c>players/&lt;slug&gt;.json</c>.</summary>
    public static string DiscovererOf(PlayerContext player) =>
        player.Name.Length == 0 ? LocalDiscoverer : WorldNames.Slug(player.Name);

    /// <summary>Whether a whole waystone stands at the loaded cell: the cell holds a waystone block (the lower half,
    /// the cell the registry keys it by) and, when the block has an upper half, that half stands in the loaded cell
    /// above it. A lone half (one a blast left, or a waystone saved before they stood two cells tall) is not one.</summary>
    public static bool IsWaystoneAt(ChunkManager chunks, int x, int y, int z) => StandsAt(chunks, x, y, z) == true;

    /// <summary>
    /// The registry cell of the waystone a cell holding <paramref name="id"/> belongs to: the cell itself for a waystone
    /// block (the lower half), the cell below for the upper half of one. False for any other block. Reads no chunk, so
    /// whether the waystone still stands whole is <see cref="IsWaystoneAt"/>'s question.
    /// </summary>
    public static bool TryGetWaystoneCell(ushort id, int x, int y, int z, out (int X, int Y, int Z) cell)
    {
        if (BlockType.ById[id] is { Waystone: true })
        {
            cell = (x, y, z);
            return true;
        }
        if (BlockType.ById[id] is { LowerHalfId: not 0 } upper && BlockType.ById[upper.LowerHalfId] is { Waystone: true })
        {
            cell = (x, y - 1, z);
            return true;
        }
        cell = default;
        return false;
    }

    // Whether a whole waystone stands at the cell (IsWaystoneAt), or null while a cell it needs is in an unloaded chunk
    private static bool? StandsAt(ChunkManager chunks, int x, int y, int z)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out _)) return null;
        if (BlockType.ById[id] is not { Waystone: true } type) return false;
        if (type.UpperHalfId == 0) return true;
        if (!chunks.TryGetCell(x, y + 1, z, out var above, out _)) return null;
        return above == type.UpperHalfId;
    }

    /// <summary>
    /// <paramref name="discoverer"/> activates the waystone at a loaded cell: its record, created and named at random
    /// on the first activation ever, which makes <paramref name="discoverer"/> its first discoverer. A record left
    /// lost while the cell holds a waystone again is a new waystone and is replaced. Null when the cell is not a
    /// loaded waystone block. <paramref name="firstDiscoverer"/> is whether this caller may rename it.
    /// </summary>
    public WaystoneRecord? Discover(ChunkManager chunks, int x, int y, int z, string discoverer, out bool firstDiscoverer)
    {
        firstDiscoverer = false;
        if (!IsWaystoneAt(chunks, x, y, z)) return null;
        if (!_records.TryGetValue((x, y, z), out var record) || record.Lost)
        {
            record = new WaystoneRecord(WaystoneNames.Generate(_rng), discoverer);
            _records[(x, y, z)] = record;
            Version++;
        }
        firstDiscoverer = record.FirstDiscoverer == discoverer;
        return record;
    }

    /// <summary>
    /// <paramref name="player"/> activates the waystone at a loaded cell (<see cref="Discover"/> under its
    /// <see cref="DiscovererOf"/> id) and the waystone joins the player's discovered list under its current name. Null
    /// when the cell is not a loaded waystone block.
    /// </summary>
    public WaystoneDiscovery? DiscoverFor(ChunkManager chunks, PlayerContext player, int x, int y, int z)
    {
        if (Discover(chunks, x, y, z, DiscovererOf(player), out var first) is not { } record) return null;
        var added = player.Inventory.Waystones.Add(x, y, z, record.Name);
        return new WaystoneDiscovery(record, first, added);
    }

    /// <summary>
    /// <paramref name="player"/> renames the waystone at the cell (<see cref="TryRename"/> under its
    /// <see cref="DiscovererOf"/> id); on success the player's own discovered entry takes the new name too.
    /// </summary>
    public bool TryRenameFor(PlayerContext player, int x, int y, int z, string? name)
    {
        if (!TryRename(x, y, z, DiscovererOf(player), name)) return false;
        player.Inventory.Waystones.Rename(x, y, z, _records[(x, y, z)].Name);
        return true;
    }

    /// <summary>Whether <paramref name="discoverer"/> may rename the waystone at the cell: it has a record, it is not
    /// lost and this is its first discoverer.</summary>
    public bool MayRename(int x, int y, int z, string discoverer) =>
        _records.TryGetValue((x, y, z), out var record) && !record.Lost && record.FirstDiscoverer == discoverer;

    /// <summary>Renames the waystone at the cell to the sanitized <paramref name="name"/>
    /// (<see cref="WaystoneNames.TrySanitize"/>). Refused, changing nothing, unless <see cref="MayRename"/> holds and
    /// something is left of the name.</summary>
    public bool TryRename(int x, int y, int z, string discoverer, string? name)
    {
        if (!MayRename(x, y, z, discoverer) || !WaystoneNames.TrySanitize(name, out var clean)) return false;
        var record = _records[(x, y, z)];
        if (record.Name == clean) return true;
        record.Name = clean;
        Version++;
        return true;
    }

    /// <summary>A waystone block was removed at the cell: its record, if it has one, is marked lost.</summary>
    public void NotifyBlockRemoved(int x, int y, int z)
    {
        if (!_records.TryGetValue((x, y, z), out var record) || record.Lost) return;
        record.Lost = true;
        Version++;
    }

    /// <summary>
    /// Marks lost every record whose waystone stopped standing whole (<see cref="IsWaystoneAt"/>) without going through
    /// a break (a blast took either half); a waystone with a cell in an unloaded chunk is left alone until it is back.
    /// </summary>
    public void Update(ChunkManager chunks)
    {
        foreach (var (key, record) in _records)
        {
            if (record.Lost || StandsAt(chunks, key.X, key.Y, key.Z) != false) continue;
            record.Lost = true;
            Version++;
        }
    }

    /// <summary>Every record, for <c>stations.json</c>.</summary>
    public List<StationSaveData.WaystoneData> Capture()
    {
        var list = new List<StationSaveData.WaystoneData>(_records.Count);
        foreach (var (key, record) in _records)
            list.Add(new StationSaveData.WaystoneData
            {
                Cell = [key.X, key.Y, key.Z],
                Name = record.Name,
                Discoverer = record.FirstDiscoverer,
                Lost = record.Lost,
            });
        return list;
    }

    /// <summary>Restores saved records into the empty manager; returns how many entries were skipped (no cell, no
    /// name left after <see cref="WaystoneNames.TrySanitize"/>, or no discoverer).</summary>
    public int Restore(IReadOnlyList<StationSaveData.WaystoneData>? saved)
    {
        if (saved == null) return 0;
        var skipped = 0;
        foreach (var data in saved)
        {
            if (data == null || !StationSaveCodec.TryCell(data.Cell, out var key)
                || !WaystoneNames.TrySanitize(data.Name, out var name) || string.IsNullOrEmpty(data.Discoverer))
            {
                skipped++;
                continue;
            }
            _records[key] = new WaystoneRecord(name, data.Discoverer, data.Lost);
        }
        Version++;
        return skipped;
    }
}
