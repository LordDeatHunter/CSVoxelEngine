using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using VoxelEngine.Chunks;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Lanterns;

/// <summary>Authoritative placed lantern ownership, access, ticking and persistence. Unloaded lanterns pause.</summary>
public sealed class LanternManager
{
    private readonly Dictionary<(int X, int Y, int Z), ItemStack> _placed = new();
    private readonly List<(int X, int Y, int Z)> _stale = [];
    public bool HasAny => _placed.Count > 0;
    public ItemStack? Peek((int X, int Y, int Z) cell) => _placed.GetValueOrDefault(cell);

    public ItemStack? Open(ChunkManager chunks, (int X, int Y, int Z) cell)
    {
        if (!chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _) || !LanternState.IsLantern(id)) return null;
        if (_placed.TryGetValue(cell, out var stack)) return stack;
        stack = new ItemStack(0, id);
        _placed.Add(cell, stack);
        return stack;
    }

    public LanternState? Resolve(PlayerContext player, ChunkManager chunks, int instance, (int X, int Y, int Z) cell)
    {
        if (!player.Present || player.Health?.IsDead == true) return null;
        if (instance > 0)
            return player.Inventory.SelectedToolInstance == instance ? LanternState.Held(player.Inventory) : null;
        return ChestAccess.InReach(player.Eye, cell.X, cell.Y, cell.Z, World.InteractionReach)
            ? LanternState.Of(Open(chunks, cell)) : null;
    }

    /// <summary>Transfers ownership on placement, including creative: a fueled lantern is never cloned for free.</summary>
    public void Place(Inventory inventory, Vector3 cell)
    {
        var stack = inventory.GetToolStack(inventory.SelectedToolInstance);
        if (LanternState.Of(stack) == null) return;
        _placed[cell.BlockCell()] = stack!.CloneAs(0);
        inventory.RemoveTool(stack.InstanceId);
    }

    public ItemStack? Take((int X, int Y, int Z) cell) => _placed.Remove(cell, out var stack) ? stack : null;

    internal void UpdateHeld(float seconds, World world, bool serverMode)
    {
        if (!serverMode && !world.LocalPlayer.LocalContext().Health!.IsDead) LanternState.Held(world.Inventory)?.Advance(seconds);
        foreach (var holder in world.ServerPlayers)
            if (!holder.IsDead && LanternState.Held(holder.Inventory) is { } lantern)
            {
                lantern.Advance(seconds);
                holder.InventoryChanged = true;
            }
    }

    /// <summary>Claims a broken lantern's one complete drop so a later stale sweep cannot duplicate it.</summary>
    internal bool DropRemoved(ushort id, Vector3 cell, DropManager drops)
    {
        if (!LanternState.IsLantern(id)) return false;
        drops.SpawnInstance(Take(cell.BlockCell()) ?? new ItemStack(0, id), cell + new Vector3(.5f));
        return true;
    }

    public void Update(float seconds, ChunkManager chunks, DropManager drops)
    {
        _stale.Clear();
        foreach (var (cell, stack) in _placed)
        {
            if (!chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _)) continue;
            if (!LanternState.IsLantern(id)) { _stale.Add(cell); continue; }
            LanternState.Of(stack)!.Advance(seconds);
        }
        foreach (var cell in _stale)
            if (Take(cell) is { } stack) drops.SpawnInstance(stack, new Vector3(cell.X + .5f, cell.Y + .5f, cell.Z + .5f));
    }

    public void AppendLights(ChunkManager chunks, List<Vector3> fireCells)
    {
        foreach (var (cell, stack) in _placed)
            if (LanternState.Of(stack)!.HasFuel && chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _)
                && LanternState.IsLantern(id))
                fireCells.Add(new Vector3(cell.X, cell.Y - .5f, cell.Z));
    }

    /// <summary>Whether the selected item emits, derived solely from owned state.</summary>
    public static bool Emits(Inventory inventory) => inventory.SelectedCount > 0
        && (LanternState.Held(inventory) is { } lantern ? lantern.HasFuel
            : BlockType.ById[inventory.SelectedBlockId]?.LightEmission > 0);

    public List<LanternData> Capture()
    {
        var data = new List<LanternData>();
        foreach (var (cell, stack) in _placed)
        {
            var entry = LanternCodec.Export(LanternState.Of(stack)!);
            entry.Cell = [cell.X, cell.Y, cell.Z];
            entry.Block = SaveNames.NameOf(stack.DefinitionId);
            data.Add(entry);
        }
        return data;
    }

    public void Restore(IReadOnlyList<LanternData>? data)
    {
        if (data == null) return;
        foreach (var entry in data)
        {
            if (!StationSaveCodec.TryCell(entry.Cell, out var cell) || !SaveNames.TryResolve(entry.Block, out var id)
                || !LanternState.IsLantern(id)) throw new System.IO.InvalidDataException("Invalid saved lantern");
            var stack = new ItemStack(0, id);
            stack.Set(new LanternComponent(LanternCodec.Import(entry)));
            if (!_placed.TryAdd(cell, stack)) throw new System.IO.InvalidDataException("Duplicate saved lantern");
        }
    }

    public void WriteNet(PacketWriter writer) => writer.WriteBytes(JsonSerializer.SerializeToUtf8Bytes(Capture()));
    public void ReadNet(PacketReader reader)
    {
        var next = new LanternManager();
        next.Restore(JsonSerializer.Deserialize<List<LanternData>>(reader.ReadBytes()));
        _placed.Clear();
        foreach (var pair in next._placed) _placed.Add(pair.Key, pair.Value);
    }
}
