using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation;

/// <summary>
/// Block-entity registry for cast molds: one pour per mold cell. Right-clicking a fired mold with
/// a crucible of molten metal binds that vessel to the cell and starts a pour, which drains it at
/// <see cref="PourMlPerSecond"/> for as long as the same crucible stays selected, alive and within
/// reach — stepping away or switching slots only pauses it, and another right-click resumes.
/// Once the mold is brim-full it cools for the recipe's <see cref="CastRecipe.CoolSeconds"/> and
/// then holds its result until a right-click collects it. Mining the mold drops a finished cast
/// and loses an unfinished one. Main thread only, like the other managers.
/// </summary>
public sealed class CastingManager
{
    /// <summary>How fast a crucible pours into a mold, in milliliters per second.</summary>
    public const float PourMlPerSecond = 100f;

    /// <summary>Live state of one mold cell: how full it is, who is pouring and how far it cooled.</summary>
    public sealed class CastState
    {
        public CastState(CastRecipe recipe)
        {
            Recipe = recipe;
        }

        /// <summary>The metalworks recipe this cell is casting; fixed for the cell's lifetime.</summary>
        public CastRecipe Recipe { get; }

        /// <summary>Milliliters poured in so far, up to the recipe's amount.</summary>
        public int FilledMl;

        /// <summary>Sub-milliliter remainder of the pour, so a fractional frame is never lost.</summary>
        public float PourCarry;

        /// <summary>Tool instance currently pouring, or 0 while the pour is paused.</summary>
        public int PourInstance;

        /// <summary>
        /// The player pouring when it is not the one <see cref="Update"/> is handed (a server's
        /// connected player): the pour drains that player's inventory, follows its selection and
        /// its eye, and pauses once it left. Null for the local player.
        /// </summary>
        public PlayerContext? PourSource;

        /// <summary>Whether the full mold is cooling down.</summary>
        public bool Cooling;

        /// <summary>Seconds of cooling left.</summary>
        public float CoolLeft;

        /// <summary>Whether the cast has cooled and is waiting to be collected.</summary>
        public bool Done;

        /// <summary>How full the pocket is, 0..1, for the liquid's rendered height.</summary>
        public float Fill01 => MathUtils.Clamp01(FilledMl / (float)Recipe.AmountMl);

        /// <summary>How far the cast has cooled, 0..1, driving the liquid-to-result dissolve.</summary>
        public float Cool01 => Done
            ? 1f
            : Cooling ? 1f - MathUtils.Clamp01(CoolLeft / Recipe.CoolSeconds) : 0f;

        /// <summary>Whether a crucible is bound and actively draining into this mold.</summary>
        public bool Pouring => PourInstance > 0;
    }

    private readonly Dictionary<(int X, int Y, int Z), CastState> _casts = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();
    private readonly List<byte> _collected = new();

    /// <summary>Whether any mold cell holds a pour, for render early-out.</summary>
    public bool HasAny => _casts.Count > 0;

    /// <summary>Live state of a mold cell, or null when nothing was ever poured into it.</summary>
    public CastState? StateAt(int x, int y, int z) => _casts.GetValueOrDefault((x, y, z));

    /// <summary>
    /// Binds <paramref name="crucible"/> to the mold at <paramref name="cell"/> and starts (or
    /// resumes) the pour. A half-filled mold takes more of the metal it was started with; an empty
    /// one takes, of the crucible's molten metals that cast in this mold, the one with the most ml
    /// (the lowest material id on a tie). Refuses anything that is not a crucible, a crucible with
    /// no molten metal this mold casts, and a mold that is cooling or waiting to be collected — a
    /// refusal never disturbs the state standing there.
    /// <paramref name="source"/> binds the pour to a player other than the one <see cref="Update"/>
    /// is handed (a server's connected player, see <see cref="CastState.PourSource"/>).
    /// </summary>
    public bool TryStartPour(Vector3 cell, byte moldId, ItemStack crucible, PlayerContext? source = null)
    {
        if (crucible.Item is not CrucibleItem) return false;
        var contents = crucible.Get<CrucibleComponent>();
        var key = VoxelCoords.BlockCellOf(cell);
        if (_casts.TryGetValue(key, out var state))
        {
            // A half-filled mold only takes more of the same metal, and never once it is set
            if (state.Cooling || state.Done || contents.MoltenOf(state.Recipe.MaterialId) <= 0) return false;
        }
        else
        {
            if (PourRecipe(moldId, contents) is not { } recipe) return false;
            state = new CastState(recipe);
            _casts[key] = state;
        }

        state.PourInstance = crucible.InstanceId;
        state.PourSource = source;
        return true;
    }

    /// <summary>The cast recipe an empty mold <paramref name="moldId"/> takes from a crucible holding
    /// <paramref name="contents"/>: the molten metal with the most ml among those it casts (the lowest
    /// material id on a tie), or null when it casts none of them.</summary>
    public static CastRecipe? PourRecipe(byte moldId, CrucibleComponent contents)
    {
        CastRecipe? best = null;
        var bestMl = 0;
        foreach (var metal in contents.Metals)
        {
            if (metal.MoltenMl <= bestMl) continue;
            if (ContentRegistry.CastRecipeFor(moldId, metal.MaterialId) is not { } recipe) continue;
            best = recipe;
            bestMl = metal.MoltenMl;
        }
        return best;
    }

    /// <summary>
    /// Runs every tracked mold: drains the bound crucible into the pocket, then — from the frame
    /// after the pocket fills — cools the cast down. The two are mutually exclusive per frame, so
    /// the Update that completes the fill never also cools (the leftover pour time in it is
    /// discarded, which is a sub-frame cosmetic and keeps the state machine unambiguous).
    /// A pour pauses, keeping everything already poured, whenever its crucible stops being the
    /// selected instance, goes out of <paramref name="reach"/>, is thrown away or runs dry.
    /// Cells that stopped being their mold (blasts, replacements) throw out a finished cast and
    /// are forgotten; unloaded cells freeze until their chunk is back.
    /// </summary>
    public void Update(
        float dt,
        ChunkManager chunks,
        DropManager drops,
        Inventory inventory,
        int selectedInstance,
        Vector3 playerPos,
        float reach)
    {
        if (dt <= 0f || _casts.Count == 0) return;
        _stale.Clear();

        foreach (var (key, state) in _casts)
        {
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _)) continue;
            if (id != state.Recipe.MoldId)
            {
                _stale.Add(key);
                continue;
            }

            if (state.Pouring)
            {
                // A pour bound to another player runs against that player, and pauses once it left
                if (state.PourSource is { } source)
                {
                    if (source.Present)
                        Pour(key, state, dt, source.Inventory, source.Inventory.SelectedToolInstance, source.Eye, reach);
                    else
                        state.PourInstance = 0;
                }
                else
                {
                    Pour(key, state, dt, inventory, selectedInstance, playerPos, reach);
                }
            }
            else if (state.Cooling)
                Cool(state, dt);
        }

        foreach (var key in _stale)
        {
            if (!_casts.TryGetValue(key, out var state)) continue;
            _casts.Remove(key);
            if (state.Done)
                SpawnResult(state, new Vector3(key.X, key.Y, key.Z), drops);
        }
    }

    private static void Pour(
        (int X, int Y, int Z) key,
        CastState state,
        float dt,
        Inventory inventory,
        int selectedInstance,
        Vector3 playerPos,
        float reach)
    {
        var stack = inventory.GetToolStack(state.PourInstance);
        var contents = stack?.Get<CrucibleComponent>() ?? default;
        var material = state.Recipe.MaterialId;
        var center = new Vector3(key.X + 0.5f, key.Y + 0.5f, key.Z + 0.5f);
        if (stack == null
            || state.PourInstance != selectedInstance
            || Vector3.Distance(playerPos, center) > reach
            || contents.MoltenOf(material) <= 0)
        {
            state.PourInstance = 0;
            return;
        }

        state.PourCarry += PourMlPerSecond * dt;
        var ml = Math.Min((int)state.PourCarry,
            Math.Min(state.Recipe.AmountMl - state.FilledMl, contents.MoltenOf(material)));
        state.PourCarry -= ml;
        state.FilledMl += ml;

        // An emptied metal leaves the vessel's books, so an empty vessel takes any charge next
        var left = contents.With(material, -ml, 0);
        stack.Set(left);
        var leftMolten = left.MoltenOf(material);

        if (state.FilledMl >= state.Recipe.AmountMl)
        {
            state.Cooling = true;
            state.CoolLeft = state.Recipe.CoolSeconds;
            state.PourInstance = 0;
            state.PourCarry = 0f;
        }
        else if (leftMolten == 0)
        {
            state.PourInstance = 0;
        }
    }

    private static void Cool(CastState state, float dt)
    {
        state.CoolLeft -= dt;
        if (state.CoolLeft > 0f) return;
        state.Cooling = false;
        state.CoolLeft = 0f;
        state.Done = true;
    }

    /// <summary>
    /// Takes a cooled cast out of the mold. Returns what came out — empty while the mold is still
    /// filling, pouring or cooling. The list is reused between calls, so a caller must consume it
    /// before the next one.
    /// </summary>
    public IReadOnlyList<byte> Collect(Vector3 cell)
    {
        _collected.Clear();
        var key = VoxelCoords.BlockCellOf(cell);
        if (!_casts.TryGetValue(key, out var state) || !state.Done) return _collected;

        for (var n = 0; n < state.Recipe.ResultCount; n++)
            _collected.Add(state.Recipe.ResultId);
        _casts.Remove(key);
        return _collected;
    }

    /// <summary>
    /// Drops a broken mold's finished cast where the block was and forgets the cell. Metal that
    /// had not set yet is simply lost with the mold.
    /// </summary>
    public void NotifyBlockRemoved(Vector3 cell, DropManager? drops = null)
    {
        var key = VoxelCoords.BlockCellOf(cell);
        if (!_casts.TryGetValue(key, out var state)) return;
        _casts.Remove(key);
        if (drops != null && state.Done)
            SpawnResult(state, new Vector3(key.X, key.Y, key.Z), drops);
    }

    /// <summary>Every mold cell holding a pour, for the molten/cooling render pass.</summary>
    public IEnumerable<(Vector3 Cell, byte MaterialId, float Fill01, float Cool01, bool Done, byte ResultId)>
        CastingView()
    {
        foreach (var (key, state) in _casts)
            yield return (new Vector3(key.X, key.Y, key.Z), state.Recipe.MaterialId,
                state.Fill01, state.Cool01, state.Done, state.Recipe.ResultId);
    }

    /// <summary>Every mold cell holding a pour by content name, for <c>stations.json</c> (at most
    /// <see cref="StationNetCodec.MaxEntries"/>). A pour in progress is saved paused: the crucible
    /// binding is transient and a right-click resumes it.</summary>
    internal List<StationSaveData.CastData> Capture()
    {
        var result = new List<StationSaveData.CastData>();
        foreach (var ((x, y, z), state) in _casts)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (SaveNames.NameOf(state.Recipe.MoldId) is not { } mold
                || SaveNames.MaterialNameOf(state.Recipe.MaterialId) is not { } material)
                continue;
            result.Add(new StationSaveData.CastData
            {
                Cell = [x, y, z],
                Mold = mold,
                Material = material,
                FilledMl = state.FilledMl,
                Cooling = state.Cooling,
                CoolLeft = state.CoolLeft,
                Done = state.Done,
            });
        }
        return result;
    }

    /// <summary>Adds the casts saved by <see cref="Capture"/> to an empty registry, all paused. An
    /// entry with a bad cell, a name that no longer resolves or no cast recipe for its mold and
    /// metal is dropped; returns how many were.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.CastData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key)
                || !SaveNames.TryResolve(data.Mold, out var mold)
                || !SaveNames.TryResolveMaterial(data.Material, out var material)
                || ContentRegistry.CastRecipeFor(mold, material) is not { } recipe
                || data.FilledMl < 0 || !float.IsFinite(data.CoolLeft))
            {
                skipped++;
                continue;
            }
            _casts[key] = new CastState(recipe)
            {
                FilledMl = Math.Min(data.FilledMl, recipe.AmountMl),
                Cooling = data.Cooling,
                CoolLeft = Math.Clamp(data.CoolLeft, 0f, recipe.CoolSeconds),
                Done = data.Done,
            };
        }
        return skipped;
    }

    /// <summary>Writes every mold cell holding a pour (its recipe by mold and metal, fill, cooling),
    /// for the station broadcast (<see cref="StationNetCodec"/>).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        // At most what a client accepts (StationNetCodec.MaxEntries)
        var leftCasts = Math.Min(_casts.Count, StationNetCodec.MaxEntries);
        writer.Write(leftCasts);
        foreach (var ((x, y, z), state) in _casts)
        {
            if (leftCasts-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write(state.Recipe.MoldId);
            writer.Write(state.Recipe.MaterialId);
            writer.Write(state.FilledMl);
            writer.Write(state.Pouring);
            writer.Write(state.Cooling);
            writer.Write(state.CoolLeft);
            writer.Write(state.Done);
        }
    }

    /// <summary>
    /// Replaces the tracked molds with a snapshot written by <see cref="WriteNet"/>: a multiplayer
    /// client's display-only mirror. An entry whose mold and metal name no cast recipe this client
    /// knows is skipped.
    /// </summary>
    internal void ReadNet(PacketReader reader)
    {
        _casts.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var moldId = reader.ReadByte();
            var materialId = reader.ReadByte();
            var filled = reader.ReadInt();
            var pouring = reader.ReadBool();
            var cooling = reader.ReadBool();
            var coolLeft = StationNetCodec.ReadFinite(reader);
            var done = reader.ReadBool();
            if (filled < 0)
                throw new System.IO.InvalidDataException("cast snapshot with negative fill");
            var recipe = ContentRegistry.CastRecipeFor(moldId, materialId);
            if (recipe == null)
                continue;
            _casts[key] = new CastState(recipe)
            {
                FilledMl = Math.Min(filled, recipe.AmountMl),
                // Display only: the mirror never drains anything, it just knows a pour is running
                PourInstance = pouring ? 1 : 0,
                Cooling = cooling,
                CoolLeft = coolLeft,
                Done = done,
            };
        }
    }

    private static void SpawnResult(CastState state, Vector3 cell, DropManager drops)
    {
        for (var n = 0; n < state.Recipe.ResultCount; n++)
            drops.Spawn(state.Recipe.ResultId, cell);
    }
}
