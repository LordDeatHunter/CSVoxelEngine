using System;
using System.Collections.Generic;
using System.Numerics;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// Block-entity registry for campfires: lit branch piles with a per-branch burn timer.
/// A max-count branch pile is the unlit firepit; igniting it adds an entry here (the
/// "fire" toggle) without touching block ids or the state byte, so meshing is unaffected.
/// Every <see cref="BurnSeconds"/> the fire consumes one branch and pops ash, staying lit
/// until the last branch burns out (which clears the block). A burning pile can be fed
/// back up with branches while it still has at least one left.
/// Main thread only, like the other managers.
/// </summary>
public sealed class CampfireManager
{
    public const float BurnSeconds = 30f;
    public const int AshPerExtinguish = 4;

    public const int MaxCookSlots = 4;

    public static readonly Vector3[] SlotOffsets =
    [
        new(-0.25f, 0.55f, -0.25f),
        new(0.25f, 0.55f, -0.25f),
        new(-0.25f, 0.55f, 0.25f),
        new(0.25f, 0.55f, 0.25f),
    ];

    public sealed class CookSlot
    {
        public byte InputId;
        public byte OutputId;
        public int OutputCount;
        public float TimeLeft;
        public float TotalTime;
        public bool IsDone;
    }

    private readonly Dictionary<(int X, int Y, int Z), float> _lit = new();
    private readonly Dictionary<(int X, int Y, int Z), List<CookSlot>> _cooking = new();
    private readonly List<(int X, int Y, int Z)> _scratch = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();
    private readonly List<Vector3> _cells = new();
    private readonly List<byte> _collected = new();

    public int LitCount => _lit.Count;

    /// <summary>Whether any campfire burns or cooks (the station broadcast's early-out).</summary>
    public bool HasAny => _lit.Count > 0 || _cooking.Count > 0;

    public IReadOnlyList<Vector3> LitCells => _cells;

    public bool IsLit(int x, int y, int z) => _lit.ContainsKey((x, y, z));

    public bool Ignite(int x, int y, int z)
    {
        var key = (x, y, z);
        if (_lit.ContainsKey(key)) return false;
        _lit[key] = BurnSeconds;
        RefreshCells();
        return true;
    }

    public void NotifyBlockRemoved(int x, int y, int z, DropManager? drops = null)
    {
        var key = (x, y, z);
        if (_lit.Remove(key))
            RefreshCells();
        if (drops != null)
            DropCooking(key, drops);
        else
            _cooking.Remove(key);
    }

    public IReadOnlyList<CookSlot>? CookingAt(int x, int y, int z) =>
        _cooking.TryGetValue((x, y, z), out var slots) ? slots : null;

    public bool TryPlaceFood(int x, int y, int z, byte inputId)
    {
        if (!IsLit(x, y, z)) return false;
        var recipe = ContentRegistry.CookRecipeForInput(inputId);
        if (recipe == null) return false;
        var key = (x, y, z);
        if (!_cooking.TryGetValue(key, out var slots))
        {
            slots = new List<CookSlot>(MaxCookSlots);
            _cooking[key] = slots;
        }
        if (slots.Count >= MaxCookSlots) return false;
        slots.Add(new CookSlot
        {
            InputId = inputId,
            OutputId = recipe.ResultId,
            OutputCount = recipe.ResultCount,
            TimeLeft = recipe.CookSeconds,
            TotalTime = recipe.CookSeconds,
            IsDone = false,
        });
        return true;
    }

    public IReadOnlyList<byte> CollectFinished(int x, int y, int z)
    {
        _collected.Clear();
        if (!_cooking.TryGetValue((x, y, z), out var slots)) return _collected;
        for (var i = slots.Count - 1; i >= 0; i--)
        {
            if (!slots[i].IsDone) continue;
            for (var n = 0; n < slots[i].OutputCount; n++)
                _collected.Add(slots[i].OutputId);
            slots.RemoveAt(i);
        }
        if (slots.Count == 0)
            _cooking.Remove((x, y, z));
        return _collected;
    }

    public IEnumerable<(Vector3 Cell, int Slot, byte ItemId, bool Done, float Progress)> CookingView()
    {
        foreach (var (key, slots) in _cooking)
        {
            var cell = new Vector3(key.X, key.Y, key.Z);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var shown = slot.IsDone ? slot.OutputId : slot.InputId;
                var progress = slot.TotalTime > 0f
                    ? 1f - MathUtils.Clamp01(slot.TimeLeft / slot.TotalTime)
                    : 1f;
                yield return (cell, i, shown, slot.IsDone, progress);
            }
        }
    }

    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _lit.Count == 0) return;
        _scratch.Clear();
        _stale.Clear();
        foreach (var (key, remaining) in _lit)
        {
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _)
                || BlockType.ById[id]?.Renderer != BlockRenderer.BranchPile)
            {
                _stale.Add(key);
                continue;
            }
            var left = remaining - deltaTime;
            if (left <= 0f)
                _scratch.Add(key);
            else
                _lit[key] = left;
        }
        foreach (var key in _stale)
        {
            _lit.Remove(key);
            DropCooking(key, drops);
        }
        foreach (var key in _scratch)
            ConsumeBranch(key.X, key.Y, key.Z, chunks, drops);
        if (_lit.Count > 0)
            ProgressCooking(deltaTime);
        if (_scratch.Count > 0 || _stale.Count > 0)
            RefreshCells();
    }

    private void ProgressCooking(float deltaTime)
    {
        foreach (var (key, slots) in _cooking)
        {
            if (!_lit.ContainsKey(key)) continue;
            foreach (var slot in slots)
            {
                if (slot.IsDone) continue;
                slot.TimeLeft -= deltaTime;
                if (slot.TimeLeft <= 0f)
                {
                    slot.TimeLeft = 0f;
                    slot.IsDone = true;
                }
            }
        }
    }

    private void DropCooking((int X, int Y, int Z) key, DropManager drops)
    {
        if (!_cooking.TryGetValue(key, out var slots)) return;
        _cooking.Remove(key);
        var cell = new Vector3(key.X, key.Y, key.Z);
        foreach (var slot in slots)
        {
            if (slot.IsDone)
            {
                for (var n = 0; n < slot.OutputCount; n++)
                    drops.Spawn(slot.OutputId, cell);
            }
            else
            {
                drops.Spawn(slot.InputId, cell);
            }
        }
    }

    /// <summary>
    /// Burns one branch off a lit pile and pops <see cref="AshPerExtinguish"/> ash. A pile
    /// with branches left stays lit on a fresh timer; the last branch clears the block and
    /// the fire goes out.
    /// </summary>
    private void ConsumeBranch(int x, int y, int z, ChunkManager chunks, DropManager drops)
    {
        _lit.Remove((x, y, z));
        if (!chunks.TryGetCell(x, y, z, out var id, out var state)) return;
        var pile = BlockType.ById[id];
        if (pile == null || pile.Renderer != BlockRenderer.BranchPile) return;
        var count = BranchPileGenerator.CountWithState(state, pile.StackMax);
        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null) return;
        var cell = new Vector3(x, y, z);
        if (count > 1)
        {
            chunk.SetStateAt(cell, (byte)(count - 1), chunks);
            _lit[(x, y, z)] = BurnSeconds;
        }
        else
        {
            chunk.RemoveBlockAt(cell, chunks);
            DropCooking((x, y, z), drops);
        }
        try
        {
            var ash = ContentRegistry.RequireItem("ash").Id;
            for (var i = 0; i < AshPerExtinguish; i++)
                drops.Spawn(ash, cell);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshCells()
    {
        _cells.Clear();
        foreach (var (x, y, z) in _lit.Keys)
            _cells.Add(new Vector3(x, y, z));
    }

    /// <summary>Writes every lit pile (with its burn timer) and every cooking slot, for the
    /// station broadcast (<see cref="StationNetCodec"/>).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        // At most what a client accepts (StationNetCodec.MaxEntries)
        var leftLit = Math.Min(_lit.Count, StationNetCodec.MaxEntries);
        writer.Write(leftLit);
        foreach (var ((x, y, z), left) in _lit)
        {
            if (leftLit-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write(left);
        }

        // At most what a client accepts (StationNetCodec.MaxEntries)
        var leftCooking = Math.Min(_cooking.Count, StationNetCodec.MaxEntries);
        writer.Write(leftCooking);
        foreach (var ((x, y, z), slots) in _cooking)
        {
            if (leftCooking-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write((byte)slots.Count);
            foreach (var slot in slots)
            {
                writer.Write(slot.InputId);
                writer.Write(slot.OutputId);
                writer.Write((byte)Math.Clamp(slot.OutputCount, 0, byte.MaxValue));
                writer.Write(slot.TimeLeft);
                writer.Write(slot.TotalTime);
                writer.Write(slot.IsDone);
            }
        }
    }

    /// <summary>Replaces every lit pile and cooking slot with a snapshot written by
    /// <see cref="WriteNet"/>: a multiplayer client's display-only mirror of the server's fires.</summary>
    internal void ReadNet(PacketReader reader)
    {
        _lit.Clear();
        _cooking.Clear();
        var lit = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < lit; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            _lit[key] = StationNetCodec.ReadFinite(reader);
        }

        var cooking = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < cooking; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var count = reader.ReadByte();
            if (count > MaxCookSlots)
                throw new System.IO.InvalidDataException($"campfire with {count} cooking slots");
            var slots = new List<CookSlot>(MaxCookSlots);
            for (var s = 0; s < count; s++)
            {
                slots.Add(new CookSlot
                {
                    InputId = reader.ReadByte(),
                    OutputId = reader.ReadByte(),
                    OutputCount = reader.ReadByte(),
                    TimeLeft = StationNetCodec.ReadFinite(reader),
                    TotalTime = StationNetCodec.ReadFinite(reader),
                    IsDone = reader.ReadBool(),
                });
            }
            if (slots.Count > 0)
                _cooking[key] = slots;
        }
        RefreshCells();
    }
}
