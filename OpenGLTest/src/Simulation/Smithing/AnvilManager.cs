using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>What one hammer hit on an anvil did.</summary>
public enum AnvilHitKind
{
    /// <summary>Nothing happened: no anvil, no recipe picked on it, or the last hit was too recent.</summary>
    Refused,

    /// <summary>The hit landed and the work is not done yet.</summary>
    Struck,

    /// <summary>The last hit landed: the recipe's metal left the stack as its result and its byproduct.</summary>
    Finished,
}

/// <summary>The outcome of <see cref="AnvilManager.TryHit"/>; the ids and counts are only meaningful for
/// <see cref="AnvilHitKind.Finished"/>.</summary>
public readonly record struct AnvilHit(AnvilHitKind Kind, ushort ResultId = 0, int ResultCount = 0,
    ushort ByproductId = 0, int ByproductCount = 0);

/// <summary>
/// Block-entity registry for anvils, keyed by the anvil's cell. An anvil holds a stack of one forge metal (blooms, or
/// vulcanite: any item a <see cref="ForgeRecipe"/> takes), stacked one per right-click up to its largest recipe
/// (<see cref="StackCap"/>). A recipe <see cref="Offered"/> for the stack is picked from the anvil's list
/// (<see cref="TrySelect"/>, which costs nothing yet) and then hammered: every <see cref="TryHit"/> counts, with a gap
/// the hammer sets between two hits (<see cref="State.CooldownLeft"/> runs down in <see cref="Update"/>, so the pace is
/// judged by the simulation and not by a client's clock), and the hit that reaches the recipe's
/// <see cref="ForgeRecipe.Hits"/> takes its metal off the stack and hands out its result and byproduct. Main thread
/// only; saved with the world and mirrored to clients like the other stations. An entry whose block is gone is swept
/// and its stack handed back as drops.
/// </summary>
public sealed class AnvilManager
{
    /// <summary>Live state of one anvil.</summary>
    public sealed class State
    {
        /// <summary>Block id of the anvil this entry was registered with.</summary>
        public ushort BlockId;

        /// <summary>The forge metal stacked on the anvil, 0 when there is no stack.</summary>
        public ushort MetalId;

        /// <summary>How many of <see cref="MetalId"/> are stacked; 0 when there is no stack.</summary>
        public int MetalCount;

        /// <summary>The result of the forge recipe picked for the stack, 0 when none is.</summary>
        public ushort SelectedResultId;

        /// <summary>Hits the picked recipe has taken.</summary>
        public int Hits;

        /// <summary>Seconds until the next hit counts.</summary>
        public float CooldownLeft;

        /// <summary>Whether nothing lies on the anvil.</summary>
        public bool IsEmpty => MetalCount == 0;

        /// <summary>The picked recipe, or null when none is.</summary>
        public ForgeRecipe? Selected =>
            SelectedResultId == 0 ? null : ContentRegistry.ForgeRecipeForResult(SelectedResultId);

        /// <summary>The recipe making <paramref name="resultId"/> when it is <see cref="Offered"/> for this stack, or
        /// null.</summary>
        public ForgeRecipe? Offers(ushort resultId) =>
            Offered(MetalId, MetalCount).FirstOrDefault(r => r.ResultId == resultId);

        // Forgets the pick and its hits
        internal void ClearSelection()
        {
            SelectedResultId = 0;
            Hits = 0;
        }
    }

    private readonly Dictionary<(int X, int Y, int Z), State> _anvils = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    /// <summary>Whether any anvil is tracked.</summary>
    public bool HasAny => _anvils.Count > 0;

    /// <summary>Registers an anvil (or refreshes the block id of one already known).</summary>
    public void Ensure(Vector3 cell, ushort blockId)
    {
        var key = KeyOf(cell);
        if (!_anvils.TryGetValue(key, out var state))
        {
            state = new State();
            _anvils[key] = state;
        }
        state.BlockId = blockId;
    }

    /// <summary>The most of <paramref name="metalId"/> an anvil stacks: the most any of its forge recipes takes, so a
    /// stack never holds metal no recipe could use. 0 for an id that is no forge metal.</summary>
    public static int StackCap(ushort metalId)
    {
        var cap = 0;
        foreach (var recipe in ContentRegistry.ForgeRecipes)
            if (recipe.MetalId == metalId)
                cap = Math.Max(cap, recipe.MetalCount);
        return cap;
    }

    /// <summary>The forge recipes a stack of <paramref name="count"/> <paramref name="metalId"/> can make: those of that
    /// metal taking no more than the stack holds, in file-name order. Empty for no stack.</summary>
    public static IReadOnlyList<ForgeRecipe> Offered(ushort metalId, int count) =>
        metalId == 0 || count <= 0
            ? []
            : ContentRegistry.ForgeRecipes.Where(r => r.MetalId == metalId && r.MetalCount <= count).ToArray();

    /// <summary>Live state of the anvil at this cell, or null when none is tracked.</summary>
    public State? StateAt(int x, int y, int z) => _anvils.GetValueOrDefault((x, y, z));

    /// <summary>Every tracked anvil, for the render pass.</summary>
    public IEnumerable<(Vector3 Cell, State State)> View()
    {
        foreach (var (key, state) in _anvils)
            yield return (new Vector3(key.X, key.Y, key.Z), state);
    }

    /// <summary>Stacks one <paramref name="metal"/> on the anvil. Refuses an id no forge recipe takes, a stack of
    /// another metal, and a stack already at its <see cref="StackCap"/>. The pick, if any, stands.</summary>
    public bool TryStack(Vector3 cell, ushort metal)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state)) return false;
        if (state.MetalCount > 0 && state.MetalId != metal) return false;
        if (state.MetalCount >= StackCap(metal)) return false;
        state.MetalId = metal;
        state.MetalCount++;
        return true;
    }

    /// <summary>
    /// Takes back one of the stack, or the whole stack when <paramref name="all"/>. A pick the smaller stack can no
    /// longer pay for is forgotten with its hits. False on an empty anvil.
    /// </summary>
    public bool TryTake(Vector3 cell, bool all, out ushort item, out int count)
    {
        item = 0;
        count = 0;
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || state.IsEmpty) return false;
        item = state.MetalId;
        count = all ? state.MetalCount : 1;
        TakeMetal(state, count);
        return true;
    }

    /// <summary>
    /// Picks the recipe making <paramref name="resultId"/> for the anvil's stack, to be hammered out next: refused
    /// unless it is <see cref="Offered"/> for the stack. A new pick starts its hits from 0; picking the one already
    /// picked keeps them. Nothing leaves the stack until the last hit.
    /// </summary>
    public bool TrySelect(Vector3 cell, ushort resultId)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state)) return false;
        if (state.Offers(resultId) == null) return false;
        if (state.SelectedResultId == resultId) return true;
        state.SelectedResultId = resultId;
        state.Hits = 0;
        return true;
    }

    private static void TakeMetal(State state, int count)
    {
        state.MetalCount -= count;
        if (state.MetalCount <= 0)
        {
            state.MetalCount = 0;
            state.MetalId = 0;
        }
        if (state.Selected is not { } picked || picked.MetalCount > state.MetalCount)
            state.ClearSelection();
    }

    /// <summary>
    /// One hammer hit. Refused unless a recipe is picked and the gap since the last hit has run down to within
    /// <paramref name="slackSeconds"/> (network jitter on a server; 0 in singleplayer). A hit that lands starts a new
    /// gap of <paramref name="gapSeconds"/> (the hammer's own pace); the one that reaches the recipe's hit count takes
    /// its metal off the stack, clears the pick and reports the result and byproduct to hand out.
    /// </summary>
    public AnvilHit TryHit(Vector3 cell, float gapSeconds, float slackSeconds)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || state.Selected is not { } recipe) return default;
        if (state.CooldownLeft > slackSeconds) return default;

        state.CooldownLeft = gapSeconds;
        state.Hits++;
        if (state.Hits < recipe.Hits)
            return new AnvilHit(AnvilHitKind.Struck);

        TakeMetal(state, recipe.MetalCount);
        state.ClearSelection();
        return new AnvilHit(AnvilHitKind.Finished, recipe.ResultId, recipe.ResultCount, recipe.ByproductId,
            recipe.ByproductCount);
    }

    /// <summary>
    /// Runs the hit gaps down and sweeps anvils whose block is gone (broken, blasted or replaced): the stack lying on
    /// one drops where it stood. An anvil in an unloaded chunk is left alone.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _anvils.Count == 0) return;
        _stale.Clear();
        foreach (var (key, state) in _anvils)
        {
            state.CooldownLeft = MathF.Max(0f, state.CooldownLeft - deltaTime);
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _) || id != state.BlockId)
                _stale.Add(key);
        }

        foreach (var key in _stale)
        {
            if (!_anvils.Remove(key, out var state)) continue;
            var at = new Vector3(key.X, key.Y, key.Z);
            for (var i = 0; i < state.MetalCount; i++)
                drops.Spawn(state.MetalId, at);
        }
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 cell) => VoxelCoords.BlockCellOf(cell);

    /// <summary>Every tracked anvil that holds a stack, by content name, for <c>stations.json</c>.</summary>
    internal List<StationSaveData.AnvilData> Capture()
    {
        var result = new List<StationSaveData.AnvilData>();
        foreach (var ((x, y, z), state) in _anvils)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (state.IsEmpty || SaveNames.NameOf(state.BlockId) is not { } block
                || SaveNames.NameOf(state.MetalId) is not { } metal)
                continue;
            result.Add(new StationSaveData.AnvilData
            {
                Cell = [x, y, z],
                Block = block,
                Metal = metal,
                Count = state.MetalCount,
                Selected = state.SelectedResultId == 0 ? null : SaveNames.NameOf(state.SelectedResultId),
                Hits = state.Hits,
                Cooldown = state.CooldownLeft,
            });
        }
        return result;
    }

    /// <summary>Adds the anvils saved by <see cref="Capture"/>, each stack clamped to its metal's
    /// <see cref="StackCap"/>. An entry whose block or metal no longer resolves or whose numbers are out of range is
    /// dropped; a pick that no longer resolves or is not offered for the stack is forgotten. Returns how many entries
    /// were dropped.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.AnvilData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (data == null || !StationSaveCodec.TryCell(data.Cell, out var key) || !SaveNames.TryResolve(data.Block, out var block)
                || RestoredState(data, block) is not { } state)
            {
                skipped++;
                continue;
            }
            _anvils[key] = state;
        }
        return skipped;
    }

    // The state one saved entry restores to, or null when its stack is out of range
    private static State? RestoredState(StationSaveData.AnvilData data, ushort block)
    {
        if (!SaveNames.TryResolve(data.Metal, out var metal) || StackCap(metal) == 0 || data.Count < 1
            || !float.IsFinite(data.Cooldown))
            return null;
        var state = new State
        {
            BlockId = block,
            MetalId = metal,
            MetalCount = Math.Min(data.Count, StackCap(metal)),
            CooldownLeft = Math.Clamp(data.Cooldown, 0f, 60f),
        };
        if (data.Selected != null && SaveNames.TryResolve(data.Selected, out var selected)
            && state.Offers(selected) is { } recipe && data.Hits >= 0 && data.Hits < recipe.Hits)
        {
            state.SelectedResultId = selected;
            state.Hits = data.Hits;
        }
        return state;
    }

    /// <summary>Writes every tracked anvil, for the station broadcast.</summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_anvils.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), state) in _anvils)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.WriteId(state.BlockId);
            writer.WriteId(state.MetalId);
            writer.Write((byte)state.MetalCount);
            writer.WriteId(state.SelectedResultId);
            writer.Write((byte)state.Hits);
            writer.Write(state.CooldownLeft);
        }
    }

    /// <summary>Replaces the tracked anvils with a snapshot written by <see cref="WriteNet"/> (a client's mirror).</summary>
    internal void ReadNet(PacketReader reader)
    {
        _anvils.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var state = new State
            {
                BlockId = BlockTables.RequireAssigned(reader.ReadId()),
                MetalId = BlockTables.RequireAssigned(reader.ReadId()),
                MetalCount = reader.ReadByte(),
                SelectedResultId = BlockTables.RequireAssigned(reader.ReadId()),
                Hits = reader.ReadByte(),
                CooldownLeft = StationNetCodec.ReadFinite(reader),
            };
            if ((state.MetalId == 0) != (state.MetalCount == 0)
                || (state.SelectedResultId != 0 && (state.Offers(state.SelectedResultId) is not { } recipe
                                                    || state.Hits >= recipe.Hits))
                || (state.SelectedResultId == 0 && state.Hits != 0))
                throw new System.IO.InvalidDataException("anvil snapshot out of range");
            _anvils[key] = state;
        }
    }
}
