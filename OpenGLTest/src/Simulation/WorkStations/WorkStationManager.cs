using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.WorkStations;

/// <summary>What one tool hit on a work station did.</summary>
public enum WorkHitKind
{
    /// <summary>Nothing happened: no station, no recipe to work on it, or the last hit was too recent.</summary>
    Refused,

    /// <summary>The hit landed and the work is not done yet.</summary>
    Struck,

    /// <summary>The last hit landed: the recipe's input left the stack as its result and its byproduct.</summary>
    Finished,
}

/// <summary>The outcome of <see cref="WorkStationManager.TryHit"/>; the ids and counts are only meaningful for
/// <see cref="WorkHitKind.Finished"/>. <see cref="ResultCount"/> is what drops now: 0 when the result stays on the
/// station waiting for its <see cref="WorkRecipe.CollectWithId"/>.</summary>
public readonly record struct WorkHit(WorkHitKind Kind, ushort ResultId = 0, int ResultCount = 0,
    ushort ByproductId = 0, int ByproductCount = 0);

/// <summary>
/// Block-entity registry for work stations (the anvil, the fleshing beam, the leather table: every block with a
/// <see cref="BlockType.WorkStation"/> profile), keyed by the station's cell. A station holds a stack of one input its
/// recipe folder takes (blooms on an anvil, raw hides on a beam, leather on a table), stacked one per right-click up to
/// its largest recipe (<see cref="StackCap"/>). A recipe <see cref="Offered"/> for the stack is picked from the station's
/// list (<see cref="TrySelect"/>, which costs nothing yet; a station offering just one needs no pick) and then worked:
/// every <see cref="TryHit"/> counts, with a gap the tool sets between two hits (<see cref="State.CooldownLeft"/> runs
/// down in <see cref="Update"/>, so the pace is judged by the simulation and not by a client's clock), and the hit that
/// reaches the recipe's <see cref="WorkRecipe.Hits"/> takes its input off the stack and hands out its result and
/// byproduct. A recipe with a <see cref="WorkRecipe.CollectWithId"/> keeps its result on the station instead (a scraped
/// hide waits on the beam for salt) until <see cref="TryCollect"/>. Main thread only; saved with the world and mirrored
/// to clients like the other stations. An entry whose block is gone is swept and its stack handed back as drops.
/// </summary>
public sealed class WorkStationManager
{
    /// <summary>The most finished results waiting on one station: the count rides the wire in a byte.</summary>
    public const int FinishedCap = 64;

    /// <summary>Live state of one work station.</summary>
    public sealed class State
    {
        /// <summary>Block id of the station this entry was registered with.</summary>
        public ushort BlockId;

        /// <summary>The input stacked on the station, 0 when there is no stack.</summary>
        public ushort InputId;

        /// <summary>How many of <see cref="InputId"/> are stacked; 0 when there is no stack.</summary>
        public int InputCount;

        /// <summary>The result of the recipe picked for the stack, 0 when none is.</summary>
        public ushort SelectedResultId;

        /// <summary>Hits the picked recipe has taken.</summary>
        public int Hits;

        /// <summary>Seconds until the next hit counts.</summary>
        public float CooldownLeft;

        /// <summary>The finished result waiting for its collect item, 0 when none waits.</summary>
        public ushort FinishedId;

        /// <summary>How many of <see cref="FinishedId"/> wait on the station.</summary>
        public int FinishedCount;

        /// <summary>The recipe folder of the station's block, or null when the block is no work station.</summary>
        public string? Station => BlockType.ById[BlockId]?.WorkStation?.Recipes;

        /// <summary>Whether nothing lies on the station.</summary>
        public bool IsEmpty => InputCount == 0 && FinishedCount == 0;

        /// <summary>The picked recipe, or null when none is.</summary>
        public WorkRecipe? Selected =>
            SelectedResultId == 0 ? null : ContentRegistry.WorkRecipeForResult(SelectedResultId);

        /// <summary>The recipe the next hit works: the picked one, or the only one offered for the stack when the
        /// station offers just one (a fleshing beam). Null when there is nothing to work.</summary>
        public WorkRecipe? Working => Selected ?? (OfferedHere is [var only] ? only : null);

        /// <summary>The recipes offered for this station's stack.</summary>
        public IReadOnlyList<WorkRecipe> OfferedHere => Offered(Station, InputId, InputCount);

        /// <summary>The recipe making <paramref name="resultId"/> when it is <see cref="Offered"/> for this stack, or
        /// null.</summary>
        public WorkRecipe? Offers(ushort resultId) => OfferedHere.FirstOrDefault(r => r.ResultId == resultId);

        // Forgets the pick and its hits
        internal void ClearSelection()
        {
            SelectedResultId = 0;
            Hits = 0;
        }
    }

    private readonly Dictionary<(int X, int Y, int Z), State> _stations = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    /// <summary>Whether any station is tracked.</summary>
    public bool HasAny => _stations.Count > 0;

    /// <summary>Registers a station (or refreshes the block id of one already known).</summary>
    public void Ensure(Vector3 cell, ushort blockId)
    {
        var key = KeyOf(cell);
        if (!_stations.TryGetValue(key, out var state))
        {
            state = new State();
            _stations[key] = state;
        }
        state.BlockId = blockId;
    }

    /// <summary>The most of <paramref name="inputId"/> a <paramref name="station"/> stacks: the most any of its recipes
    /// takes, so a stack never holds input no recipe could use. 0 for an id that is no input there.</summary>
    public static int StackCap(string? station, ushort inputId)
    {
        var cap = 0;
        foreach (var recipe in ContentRegistry.WorkRecipes)
            if (recipe.InputId == inputId && recipe.Station == station)
                cap = Math.Max(cap, recipe.InputCount);
        return cap;
    }

    /// <summary>The recipes of <paramref name="station"/> a stack of <paramref name="count"/>
    /// <paramref name="inputId"/> can make: those of that input taking no more than the stack holds, in file-name
    /// order. Empty for no stack.</summary>
    public static IReadOnlyList<WorkRecipe> Offered(string? station, ushort inputId, int count) =>
        station == null || inputId == 0 || count <= 0
            ? []
            : ContentRegistry.WorkRecipes
                .Where(r => r.Station == station && r.InputId == inputId && r.InputCount <= count).ToArray();

    /// <summary>Live state of the station at this cell, or null when none is tracked.</summary>
    public State? StateAt(int x, int y, int z) => _stations.GetValueOrDefault((x, y, z));

    /// <summary>Every tracked station, for the render pass.</summary>
    public IEnumerable<(Vector3 Cell, State State)> View()
    {
        foreach (var (key, state) in _stations)
            yield return (new Vector3(key.X, key.Y, key.Z), state);
    }

    /// <summary>The most of <paramref name="inputId"/> this station stacks: its folder's <see cref="StackCap"/>, raised to
    /// its block's own <see cref="WorkStationProfile.Stack"/>; 0 for an id that is no input there.</summary>
    public static int CapOf(State state, ushort inputId)
    {
        var cap = StackCap(state.Station, inputId);
        return cap == 0 ? 0 : Math.Max(cap, BlockType.ById[state.BlockId]?.WorkStation?.Stack ?? 0);
    }

    /// <summary>Stacks one <paramref name="input"/> on the station. Refuses an id none of its recipes takes, a stack of
    /// another input, and a stack already at its <see cref="CapOf"/>. The pick, if any, stands.</summary>
    public bool TryStack(Vector3 cell, ushort input)
    {
        if (!_stations.TryGetValue(KeyOf(cell), out var state)) return false;
        if (state.InputCount > 0 && state.InputId != input) return false;
        if (state.InputCount >= CapOf(state, input)) return false;
        state.InputId = input;
        state.InputCount++;
        return true;
    }

    /// <summary>
    /// Takes back one of the stack, or the whole stack when <paramref name="all"/>. A pick the smaller stack can no
    /// longer pay for is forgotten with its hits. False with no stack (a finished result waiting is not the stack).
    /// </summary>
    public bool TryTake(Vector3 cell, bool all, out ushort item, out int count)
    {
        item = 0;
        count = 0;
        if (!_stations.TryGetValue(KeyOf(cell), out var state) || state.InputCount == 0) return false;
        item = state.InputId;
        count = all ? state.InputCount : 1;
        TakeInput(state, count);
        return true;
    }

    /// <summary>
    /// Hands over one finished result waiting on the station, when <paramref name="heldId"/> is the item its recipe
    /// waits for (<see cref="WorkRecipe.CollectWithId"/>); the caller spends one of it. False otherwise.
    /// </summary>
    public bool TryCollect(Vector3 cell, ushort heldId, out ushort result)
    {
        result = 0;
        if (heldId == 0 || !_stations.TryGetValue(KeyOf(cell), out var state) || state.FinishedCount == 0) return false;
        if (ContentRegistry.WorkRecipeForResult(state.FinishedId) is not { } recipe || recipe.CollectWithId != heldId)
            return false;
        result = state.FinishedId;
        if (--state.FinishedCount == 0) state.FinishedId = 0;
        return true;
    }

    /// <summary>
    /// Picks the recipe making <paramref name="resultId"/> for the station's stack, to be worked next: refused unless it
    /// is <see cref="Offered"/> for the stack. A new pick starts its hits from 0; picking the one already picked keeps
    /// them. Nothing leaves the stack until the last hit.
    /// </summary>
    public bool TrySelect(Vector3 cell, ushort resultId)
    {
        if (!_stations.TryGetValue(KeyOf(cell), out var state)) return false;
        if (state.Offers(resultId) == null) return false;
        if (state.SelectedResultId == resultId) return true;
        state.SelectedResultId = resultId;
        state.Hits = 0;
        return true;
    }

    private static void TakeInput(State state, int count)
    {
        state.InputCount -= count;
        if (state.InputCount <= 0)
        {
            state.InputCount = 0;
            state.InputId = 0;
        }
        if (state.Selected is not { } picked || picked.InputCount > state.InputCount)
            state.ClearSelection();
    }

    /// <summary>
    /// One tool hit. Refused unless there is a recipe to work (<see cref="State.Working"/>: the pick, or the only recipe
    /// offered, which the hit picks) and the gap since the last hit has run down to within
    /// <paramref name="slackSeconds"/> (network jitter on a server; 0 in singleplayer). A result that would wait on the
    /// station is refused while another result waits there or the waiting pile is full. A hit that lands starts a new
    /// gap of <paramref name="gapSeconds"/> (the tool's own pace); the one that reaches the recipe's hit count takes
    /// its input off the stack, clears the pick and reports the result to drop (or keeps it waiting) and the byproduct.
    /// </summary>
    public WorkHit TryHit(Vector3 cell, float gapSeconds, float slackSeconds)
    {
        if (!_stations.TryGetValue(KeyOf(cell), out var state) || state.Working is not { } recipe) return default;
        if (state.CooldownLeft > slackSeconds) return default;
        if (recipe.CollectWithId != 0
            && ((state.FinishedCount > 0 && state.FinishedId != recipe.ResultId)
                || state.FinishedCount + recipe.ResultCount > FinishedCap))
            return default;

        state.SelectedResultId = recipe.ResultId;
        state.CooldownLeft = gapSeconds;
        state.Hits++;
        if (state.Hits < recipe.Hits)
            return new WorkHit(WorkHitKind.Struck);

        TakeInput(state, recipe.InputCount);
        state.ClearSelection();
        var dropped = recipe.ResultCount;
        if (recipe.CollectWithId != 0)
        {
            state.FinishedId = recipe.ResultId;
            state.FinishedCount += recipe.ResultCount;
            dropped = 0;
        }
        return new WorkHit(WorkHitKind.Finished, recipe.ResultId, dropped, recipe.ByproductId, recipe.ByproductCount);
    }

    /// <summary>
    /// Runs the hit gaps down and sweeps stations whose block is gone (broken, blasted or replaced): the stack lying on
    /// one drops where it stood, and a finished result still waiting drops as the input it was worked from (a scraped
    /// hide never leaves a beam unsalted). A station in an unloaded chunk is left alone.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _stations.Count == 0) return;
        _stale.Clear();
        foreach (var (key, state) in _stations)
        {
            state.CooldownLeft = MathF.Max(0f, state.CooldownLeft - deltaTime);
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _) || id != state.BlockId)
                _stale.Add(key);
        }

        foreach (var key in _stale)
        {
            if (!_stations.Remove(key, out var state)) continue;
            var at = new Vector3(key.X, key.Y, key.Z);
            for (var i = 0; i < state.InputCount; i++)
                drops.Spawn(state.InputId, at);
            if (ContentRegistry.WorkRecipeForResult(state.FinishedId) is not { } recipe) continue;
            var back = state.FinishedCount * recipe.InputCount / recipe.ResultCount;
            for (var i = 0; i < back; i++)
                drops.Spawn(recipe.InputId, at);
        }
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 cell) => VoxelCoords.BlockCellOf(cell);

    /// <summary>Every tracked station that holds anything, by content name, for <c>stations.json</c>.</summary>
    internal List<StationSaveData.AnvilData> Capture()
    {
        var result = new List<StationSaveData.AnvilData>();
        foreach (var ((x, y, z), state) in _stations)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (state.IsEmpty || SaveNames.NameOf(state.BlockId) is not { } block) continue;
            result.Add(new StationSaveData.AnvilData
            {
                Cell = [x, y, z],
                Block = block,
                Metal = state.InputCount == 0 ? null : SaveNames.NameOf(state.InputId),
                Count = state.InputCount,
                Selected = state.SelectedResultId == 0 ? null : SaveNames.NameOf(state.SelectedResultId),
                Hits = state.Hits,
                Cooldown = state.CooldownLeft,
                Finished = state.FinishedCount == 0 ? null : SaveNames.NameOf(state.FinishedId),
                FinishedCount = state.FinishedCount,
            });
        }
        return result;
    }

    /// <summary>Adds the stations saved by <see cref="Capture"/>, each stack clamped to its
    /// <see cref="CapOf"/> and each waiting pile to <see cref="FinishedCap"/>. An entry whose block no longer resolves
    /// to a work station, or that holds nothing valid, is dropped; a pick that no longer resolves or is not offered for
    /// the stack is forgotten, and so is a waiting result that no recipe of the station keeps waiting. Returns how many
    /// entries were dropped.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.AnvilData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key) || !SaveNames.TryResolve(data.Block, out var block)
                || RestoredState(data, block) is not { } state)
            {
                skipped++;
                continue;
            }
            _stations[key] = state;
        }
        return skipped;
    }

    // The state one saved entry restores to, or null when it holds nothing in range
    private static State? RestoredState(StationSaveData.AnvilData data, ushort block)
    {
        if (!float.IsFinite(data.Cooldown)) return null;
        var state = new State { BlockId = block, CooldownLeft = Math.Clamp(data.Cooldown, 0f, 60f) };
        if (state.Station == null) return null;

        if (data.Metal != null && SaveNames.TryResolve(data.Metal, out var input)
            && CapOf(state, input) is var cap and > 0 && data.Count >= 1)
        {
            state.InputId = input;
            state.InputCount = Math.Min(data.Count, cap);
        }
        if (data.Finished != null && SaveNames.TryResolve(data.Finished, out var finished)
            && IsWaitingResult(state.Station, finished) && data.FinishedCount >= 1)
        {
            state.FinishedId = finished;
            state.FinishedCount = Math.Min(data.FinishedCount, FinishedCap);
        }
        if (state.IsEmpty) return null;

        if (data.Selected != null && SaveNames.TryResolve(data.Selected, out var selected)
            && state.Offers(selected) is { } recipe && data.Hits >= 0 && data.Hits < recipe.Hits)
        {
            state.SelectedResultId = selected;
            state.Hits = data.Hits;
        }
        return state;
    }

    // Whether a result of this station's recipes waits on it for a collect item
    private static bool IsWaitingResult(string? station, ushort resultId) =>
        ContentRegistry.WorkRecipeForResult(resultId) is { CollectWithId: not 0 } recipe && recipe.Station == station;

    /// <summary>Writes every tracked station, for the station broadcast.</summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_stations.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), state) in _stations)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.WriteId(state.BlockId);
            writer.WriteId(state.InputId);
            writer.Write((byte)state.InputCount);
            writer.WriteId(state.SelectedResultId);
            writer.Write((byte)state.Hits);
            writer.Write(state.CooldownLeft);
            writer.WriteId(state.FinishedId);
            writer.Write((byte)state.FinishedCount);
        }
    }

    /// <summary>Replaces the tracked stations with a snapshot written by <see cref="WriteNet"/> (a client's
    /// mirror).</summary>
    internal void ReadNet(PacketReader reader)
    {
        _stations.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var state = new State
            {
                BlockId = BlockTables.RequireAssigned(reader.ReadId()),
                InputId = BlockTables.RequireAssigned(reader.ReadId()),
                InputCount = reader.ReadByte(),
                SelectedResultId = BlockTables.RequireAssigned(reader.ReadId()),
                Hits = reader.ReadByte(),
                CooldownLeft = StationNetCodec.ReadFinite(reader),
                FinishedId = BlockTables.RequireAssigned(reader.ReadId()),
                FinishedCount = reader.ReadByte(),
            };
            if (state.Station == null
                || (state.InputId == 0) != (state.InputCount == 0)
                || (state.FinishedId == 0) != (state.FinishedCount == 0)
                || (state.FinishedId != 0 && !IsWaitingResult(state.Station, state.FinishedId))
                || (state.SelectedResultId != 0 && (state.Offers(state.SelectedResultId) is not { } recipe
                                                    || state.Hits >= recipe.Hits))
                || (state.SelectedResultId == 0 && state.Hits != 0))
                throw new System.IO.InvalidDataException("work station snapshot out of range");
            _stations[key] = state;
        }
    }
}
