using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>What one hammer hit on an anvil did.</summary>
public enum AnvilHitKind
{
    /// <summary>Nothing happened: no anvil, nothing on it, or the last hit was too recent.</summary>
    Refused,

    /// <summary>The hit landed and the work is not done yet.</summary>
    Struck,

    /// <summary>The last hit landed: the piece became its result and its slag.</summary>
    Finished,
}

/// <summary>The outcome of <see cref="AnvilManager.TryHit"/>; the ids and counts are only meaningful for
/// <see cref="AnvilHitKind.Finished"/>.</summary>
public readonly record struct AnvilHit(AnvilHitKind Kind, byte ResultId = 0, int ResultCount = 0, byte SlagId = 0,
    int SlagCount = 0);

/// <summary>
/// Block-entity registry for anvils, keyed by the anvil's cell. An anvil holds <b>either</b> one piece of its profile's
/// input (a bloom, laid on with right-click) <b>or</b> a stack of one forge metal (wrought iron: any item a
/// <see cref="ForgeRecipe"/> takes, stacked one per right-click up to its largest recipe, <see cref="StackCap"/>), never
/// both. The hammer works a piece: every <see cref="TryHit"/> counts, with a gap the hammer sets between two hits
/// (<see cref="State.CooldownLeft"/> runs down in <see cref="Update"/>, so the pace is judged by the simulation and not
/// by a client's clock), and the hit that reaches <see cref="AnvilProfile.Hits"/> turns the piece into its result and
/// its slag. A stack is forged instead: the recipes <see cref="Offered"/> for its metal and its size are picked from a
/// list and <see cref="TryForge"/> takes their metal off the stack. Main thread only; saved with the world and mirrored
/// to clients like the other stations. An entry whose block is gone is swept and what lies on it handed back as drops.
/// </summary>
public sealed class AnvilManager
{
    /// <summary>Live state of one anvil.</summary>
    public sealed class State
    {
        /// <summary>Block id of the anvil this entry was registered with.</summary>
        public byte BlockId;

        /// <summary>How this anvil behaves, from <see cref="BlockId"/>; never saved or sent.</summary>
        public AnvilProfile Profile = AnvilProfile.Default;

        /// <summary>Whether a piece (a bloom) lies on the anvil; never with a metal stack.</summary>
        public bool HasPiece;

        /// <summary>Hits the piece has taken.</summary>
        public int Hits;

        /// <summary>Seconds until the next hit counts.</summary>
        public float CooldownLeft;

        /// <summary>The forge metal stacked on the anvil, 0 when there is no stack.</summary>
        public byte MetalId;

        /// <summary>How many of <see cref="MetalId"/> are stacked; 0 when there is no stack.</summary>
        public int MetalCount;

        /// <summary>Whether nothing lies on the anvil: no piece and no stack.</summary>
        public bool IsEmpty => !HasPiece && MetalCount == 0;
    }

    private readonly Dictionary<(int X, int Y, int Z), State> _anvils = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    /// <summary>Whether any anvil is tracked.</summary>
    public bool HasAny => _anvils.Count > 0;

    /// <summary>Registers an anvil (or refreshes the block id of one already known).</summary>
    public void Ensure(Vector3 cell, byte blockId)
    {
        var key = KeyOf(cell);
        if (!_anvils.TryGetValue(key, out var state))
        {
            state = new State();
            _anvils[key] = state;
        }
        state.BlockId = blockId;
        state.Profile = ProfileOf(blockId);
    }

    /// <summary>The anvil profile of block <paramref name="blockId"/>, or <see cref="AnvilProfile.Default"/> when it
    /// is not (or no longer) an anvil.</summary>
    public static AnvilProfile ProfileOf(byte blockId) => BlockType.ById[blockId]?.Anvil ?? AnvilProfile.Default;

    /// <summary>The most of <paramref name="metalId"/> an anvil stacks: the most any of its forge recipes takes, so a
    /// stack never holds metal no recipe could use. 0 for an id that is no forge metal.</summary>
    public static int StackCap(byte metalId)
    {
        var cap = 0;
        foreach (var recipe in ContentRegistry.ForgeRecipes)
            if (recipe.MetalId == metalId)
                cap = Math.Max(cap, recipe.MetalCount);
        return cap;
    }

    /// <summary>The forge recipes a stack of <paramref name="count"/> <paramref name="metalId"/> can make: those of that
    /// metal taking no more than the stack holds, in file-name order. Empty for no stack.</summary>
    public static IReadOnlyList<ForgeRecipe> Offered(byte metalId, int count) =>
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

    /// <summary>Lays one <paramref name="item"/> on an empty anvil. Refuses anything but the profile's input and an
    /// anvil that already holds a piece or a stack.</summary>
    public bool TryPlace(Vector3 cell, byte item)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || !state.IsEmpty) return false;
        if (item == 0 || item != state.Profile.InputId) return false;
        state.HasPiece = true;
        state.Hits = 0;
        state.CooldownLeft = 0f;
        return true;
    }

    /// <summary>Stacks one <paramref name="metal"/> on the anvil. Refuses an id no forge recipe takes, an anvil holding
    /// a piece or a stack of another metal, and a stack already at its <see cref="StackCap"/>.</summary>
    public bool TryStack(Vector3 cell, byte metal)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || state.HasPiece) return false;
        if (state.MetalCount > 0 && state.MetalId != metal) return false;
        if (state.MetalCount >= StackCap(metal)) return false;
        state.MetalId = metal;
        state.MetalCount++;
        return true;
    }

    /// <summary>
    /// Takes back what lies on the anvil: the piece (however many hits it took; they are lost), or one of the stack,
    /// or the whole stack when <paramref name="all"/>. False on an empty anvil.
    /// </summary>
    public bool TryTake(Vector3 cell, bool all, out byte item, out int count)
    {
        item = 0;
        count = 0;
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || state.IsEmpty) return false;
        if (state.HasPiece)
        {
            item = state.Profile.InputId;
            count = 1;
            state.HasPiece = false;
            state.Hits = 0;
            return true;
        }

        item = state.MetalId;
        count = all ? state.MetalCount : 1;
        TakeMetal(state, count);
        return true;
    }

    /// <summary>
    /// Forges the recipe making <paramref name="resultId"/> from the anvil's stack: refused unless such a recipe exists
    /// and the stack is of its metal and holds at least its count (<see cref="Offered"/>), which then leaves the stack.
    /// The caller hands out <paramref name="recipe"/>'s result.
    /// </summary>
    public bool TryForge(Vector3 cell, byte resultId, out ForgeRecipe? recipe)
    {
        recipe = ContentRegistry.ForgeRecipeForResult(resultId);
        if (recipe == null || !_anvils.TryGetValue(KeyOf(cell), out var state)) return false;
        if (state.MetalId != recipe.MetalId || state.MetalCount < recipe.MetalCount) return false;
        TakeMetal(state, recipe.MetalCount);
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
    }

    /// <summary>
    /// One hammer hit. Refused unless a piece lies on the anvil and the gap since the last hit has run down to within
    /// <paramref name="slackSeconds"/> (network jitter on a server; 0 in singleplayer). A hit that lands starts a new
    /// gap of <paramref name="gapSeconds"/> (the hammer's own pace); the one that reaches the profile's hit count
    /// finishes the piece, clears the anvil and reports the result and slag to hand out.
    /// </summary>
    public AnvilHit TryHit(Vector3 cell, float gapSeconds, float slackSeconds)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || !state.HasPiece) return default;
        if (state.CooldownLeft > slackSeconds) return default;

        state.CooldownLeft = gapSeconds;
        state.Hits++;
        if (state.Hits < state.Profile.Hits)
            return new AnvilHit(AnvilHitKind.Struck);

        state.HasPiece = false;
        state.Hits = 0;
        return new AnvilHit(AnvilHitKind.Finished, state.Profile.ResultId, BloomeryConfig.IronPerBloom,
            state.Profile.SlagId, BloomeryConfig.SlagPerBloom);
    }

    /// <summary>
    /// Runs the hit gaps down and sweeps anvils whose block is gone (broken, blasted or replaced): the piece or the
    /// stack lying on one drops where it stood. An anvil in an unloaded chunk is left alone.
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
            if (state.HasPiece)
                drops.Spawn(state.Profile.InputId, at);
            for (var i = 0; i < state.MetalCount; i++)
                drops.Spawn(state.MetalId, at);
        }
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 cell) => VoxelCoords.BlockCellOf(cell);

    /// <summary>Every tracked anvil that holds something, by content name, for <c>stations.json</c>.</summary>
    internal List<StationSaveData.AnvilData> Capture()
    {
        var result = new List<StationSaveData.AnvilData>();
        foreach (var ((x, y, z), state) in _anvils)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (state.IsEmpty || SaveNames.NameOf(state.BlockId) is not { } block) continue;
            var data = new StationSaveData.AnvilData { Cell = [x, y, z], Block = block };
            if (state.HasPiece)
            {
                data.Hits = state.Hits;
                data.Cooldown = state.CooldownLeft;
            }
            else if (SaveNames.NameOf(state.MetalId) is { } metal)
            {
                data.Metal = metal;
                data.Count = state.MetalCount;
            }
            else continue;
            result.Add(data);
        }
        return result;
    }

    /// <summary>Adds the anvils saved by <see cref="Capture"/>: one with a <see cref="StationSaveData.AnvilData.Metal"/>
    /// holds that stack (clamped to the metal's <see cref="StackCap"/>), any other a piece. An entry whose block or metal
    /// no longer resolves or whose numbers are out of range is dropped. Returns how many were dropped.</summary>
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
            _anvils[key] = state;
        }
        return skipped;
    }

    // The state one saved entry restores to, or null when its piece or stack is out of range
    private static State? RestoredState(StationSaveData.AnvilData data, byte block)
    {
        var state = new State { BlockId = block, Profile = ProfileOf(block) };
        if (data.Metal != null)
        {
            if (!SaveNames.TryResolve(data.Metal, out var metal) || StackCap(metal) == 0 || data.Count < 1) return null;
            state.MetalId = metal;
            state.MetalCount = Math.Min(data.Count, StackCap(metal));
            return state;
        }

        if (data.Hits < 0 || data.Hits >= state.Profile.Hits || !float.IsFinite(data.Cooldown)) return null;
        state.HasPiece = true;
        state.Hits = data.Hits;
        state.CooldownLeft = Math.Clamp(data.Cooldown, 0f, 60f);
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
            writer.Write(state.BlockId);
            writer.Write(state.HasPiece);
            writer.Write((byte)state.Hits);
            writer.Write(state.CooldownLeft);
            writer.Write(state.MetalId);
            writer.Write((byte)state.MetalCount);
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
                BlockId = reader.ReadByte(),
                HasPiece = reader.ReadBool(),
                Hits = reader.ReadByte(),
                CooldownLeft = StationNetCodec.ReadFinite(reader),
                MetalId = reader.ReadByte(),
                MetalCount = reader.ReadByte(),
            };
            state.Profile = ProfileOf(state.BlockId);
            if (state.Hits > AnvilProfile.MaxHits || (state.MetalId == 0) != (state.MetalCount == 0)
                || (state.HasPiece && state.MetalCount > 0))
                throw new System.IO.InvalidDataException("anvil snapshot out of range");
            _anvils[key] = state;
        }
    }
}
