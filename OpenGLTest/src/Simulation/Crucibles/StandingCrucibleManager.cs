using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Crucibles;

/// <summary>
/// Block-entity registry of crucibles standing on the ground: each cell of a <see cref="BlockType.CrucibleStand"/>
/// block holds the crucible's own <see cref="ItemStack"/>, so its metal rides with the vessel when it is set down,
/// picked up again or broken and dropped. Authoritative on a server and in singleplayer, a display-only mirror on a
/// client (<see cref="WriteNet"/>/<see cref="ReadNet"/>). Unlike a kiln it ticks nothing: the metal neither melts nor
/// cools while it stands. Main thread only, like the other managers.
/// </summary>
public sealed class StandingCrucibleManager
{
    /// <summary>The block a crucible stands as (<c>crucible_stand.json</c>).</summary>
    public const string StandBlock = "crucible_stand";

    /// <summary>The crucible item a stand falls back to when its entry was lost.</summary>
    public const string CrucibleItemName = "crucible";

    private readonly Dictionary<(int X, int Y, int Z), ItemStack> _placed = new();
    private readonly List<(int X, int Y, int Z)> _stale = [];

    /// <summary>Whether any crucible stands, for the station broadcast's early-out.</summary>
    public bool HasAny => _placed.Count > 0;

    /// <summary>Whether <paramref name="blockId"/> is a block a crucible stands as.</summary>
    public static bool IsStand(ushort blockId) => BlockType.ById[blockId]?.CrucibleStand == true;

    /// <summary>The crucible standing in a cell, or null when none is recorded.</summary>
    public ItemStack? Peek((int X, int Y, int Z) cell) => _placed.GetValueOrDefault(cell);

    /// <summary>Stands <paramref name="crucible"/> (copied, with its contents) in a cell, replacing any entry there.</summary>
    public void Place((int X, int Y, int Z) cell, ItemStack crucible) => _placed[cell] = crucible.CloneAs(0);

    /// <summary>Removes and returns the crucible of a cell: what was recorded there, or an empty crucible when the
    /// block stood without an entry, so a stand never loses the vessel.</summary>
    public ItemStack Take((int X, int Y, int Z) cell) =>
        _placed.Remove(cell, out var stack) ? stack : new ItemStack(0, ContentRegistry.RequireItem(CrucibleItemName).Id);

    /// <summary>Drops the crucible of every entry whose block is gone (a blast, a flow, a replaced cell), once its
    /// chunk is loaded; an unloaded cell freezes.</summary>
    public void Update(ChunkManager chunks, DropManager drops)
    {
        _stale.Clear();
        foreach (var cell in _placed.Keys)
            if (chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _) && !IsStand(id))
                _stale.Add(cell);
        foreach (var cell in _stale)
            drops.SpawnInstance(Take(cell), new Vector3(cell.X + .5f, cell.Y + .5f, cell.Z + .5f));
    }

    /// <summary>Every standing crucible by content name, for <c>stations.json</c> and the wire.</summary>
    public List<StandingCrucibleData> Capture()
    {
        var data = new List<StandingCrucibleData>(_placed.Count);
        foreach (var (cell, stack) in _placed)
            data.Add(new StandingCrucibleData
            {
                Cell = [cell.X, cell.Y, cell.Z],
                Item = SaveNames.NameOf(stack.DefinitionId),
                Contents = CrucibleSaveCodec.Export(stack.Get<CrucibleComponent>()),
            });
        return data;
    }

    /// <summary>Adds the crucibles saved by <see cref="Capture"/> to an empty registry. An entry with a bad cell, or a
    /// name that no longer resolves to a crucible, is dropped; returns how many were.</summary>
    public int Restore(IReadOnlyList<StandingCrucibleData>? data)
    {
        if (data == null) return 0;
        var skipped = 0;
        foreach (var entry in data)
        {
            if (entry == null || !StationSaveCodec.TryCell(entry.Cell, out var cell)
                || !SaveNames.TryResolve(entry.Item, out var id) || ItemRegistry.TryGet(id) is not CrucibleItem crucible)
            {
                skipped++;
                continue;
            }
            var stack = new ItemStack(0, id);
            stack.Set(CrucibleSaveCodec.Import(entry.Contents, crucible.CrucibleCapacityMl, []));
            _placed[cell] = stack;
        }
        return skipped;
    }

    /// <summary>Writes every standing crucible for a client's mirror.</summary>
    public void WriteNet(PacketWriter writer) => writer.WriteBytes(JsonSerializer.SerializeToUtf8Bytes(Capture()));

    /// <summary>Replaces every standing crucible with a snapshot written by <see cref="WriteNet"/>.</summary>
    public void ReadNet(PacketReader reader)
    {
        _placed.Clear();
        Restore(JsonSerializer.Deserialize<List<StandingCrucibleData>>(reader.ReadBytes()));
    }
}
