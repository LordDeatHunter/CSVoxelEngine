using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation;

/// <summary>
/// Block-entity registry for mud brick kilns, keyed by the structure's anchor cell (the volume's
/// minimum corner). How a kiln behaves comes from its anchor block's <see cref="KilnProfile"/>
/// (<see cref="KilnState.Profile"/>): it stores up to <see cref="KilnProfile.FuelCap"/> units of
/// one fuel item; striking it with a firestarter burns one unit at once and the next whenever the
/// burn timer runs out, so a plain kiln with four coal stays lit for four minutes and then goes
/// out.
///
/// <para>
/// A kiln also holds one crucible. Right-clicking a melt input into it charges the vessel with
/// <em>pending</em> metal; a lit kiln then turns <see cref="MeltChunkMl"/> of that pending charge
/// molten every <see cref="KilnProfile.MeltSeconds"/> of its profile. Melting only
/// <em>pauses</em> when the fire goes out or the vessel is taken — <see cref="KilnState.MeltLeft"/> simply stops ticking — so relighting
/// resumes the partial chunk instead of starting it over. <see cref="MeltProgress01"/> and
/// <see cref="DisplayMoltenMl"/> are what the render pass reads, and neither depends on
/// <see cref="KilnState.Lit"/>, so the pool never drops when the last unit burns out.
/// </para>
///
/// <para>
/// The per-frame <see cref="Update"/> doubles as the structure's integrity sweep: nothing but
/// mining goes through <c>BlockEditor.RemoveBlock</c>, so a blast that takes a cell (a part
/// <em>or</em> the anchor) would otherwise leave the rest standing as invisible solid blocks.
/// <see cref="KilnState.AnchorId"/> is what lets the sweep revert a kiln whose anchor cell is
/// already gone. Only kilns get this: a future multiblock that is not a kiln would need its own
/// registry, or a generic one.
/// </para>
///
/// Main thread only, like <see cref="QuernManager"/> and <see cref="CampfireManager"/>. Fuel,
/// fire, crucible and melt progress are saved to <c>stations.json</c> (<see cref="Capture"/>,
/// <see cref="Restore"/>).
/// </summary>
public sealed class KilnManager
{
    /// <summary>Milliliters that turn molten per melt interval while a kiln burns, the same for
    /// every kiln: a faster kiln (<see cref="KilnProfile.MeltSpeed"/>) shortens the interval
    /// rather than melting bigger chunks.</summary>
    public const int MeltChunkMl = 100;

    /// <summary>Live state of one kiln structure.</summary>
    public sealed class KilnState
    {
        /// <summary>Block id of the anchor this kiln was registered with. Remembered so the
        /// integrity sweep can revert the structure even when the anchor cell itself is gone.</summary>
        public byte AnchorId;

        /// <summary>How this kiln behaves, derived from <see cref="AnchorId"/>'s block whenever
        /// the kiln is registered, restored or mirrored (<see cref="ProfileOf"/>); never saved or
        /// sent.</summary>
        public KilnProfile Profile = KilnProfile.Default;

        /// <summary>Which way the opening points (engine face index 0..3).</summary>
        public int Facing;

        /// <summary>Unburnt units waiting in the hearth, 0..<see cref="KilnProfile.FuelCap"/>.</summary>
        public int Fuel;

        /// <summary>Item id of the stored fuel; 0 when the hearth is empty.</summary>
        public byte FuelItemId;

        /// <summary>Seconds one stored unit burns for.</summary>
        public float FuelSeconds;

        /// <summary>Whether the kiln is burning right now.</summary>
        public bool Lit;

        /// <summary>Seconds left on the unit currently burning.</summary>
        public float BurnLeft;

        /// <summary>Whether a crucible is standing in the hearth.</summary>
        public bool HasCrucible;

        /// <summary>Item id of that crucible; 0 when the slot is empty.</summary>
        public byte CrucibleItemId;

        /// <summary>What the crucible holds. Moves back onto the item's stack when it is taken
        /// out, and is lost when the structure is broken.</summary>
        public CrucibleComponent Contents;

        /// <summary>Seconds of fire still owed on the chunk currently melting. Counts down only
        /// while the kiln is lit, so taking the fire away pauses the chunk rather than
        /// restarting it.</summary>
        public float MeltLeft;
    }

    private readonly Dictionary<(int X, int Y, int Z), KilnState> _kilns = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();
    private readonly List<Vector3> _litCells = new();

    /// <summary>Whether any kiln is tracked, for render early-out.</summary>
    public bool HasAny => _kilns.Count > 0;

    /// <summary>
    /// Particle/light origins of every lit kiln, one entry each. The entry is the flame's world
    /// position minus <see cref="FireParticleSystem.SpawnOffset"/>, so the particle system (which
    /// adds that offset back) spawns exactly at its layout's <see cref="KilnLayout.FireLocal"/> and the point
    /// light lands the usual 0.65 blocks above it — still under the kiln's lintel.
    /// </summary>
    public IReadOnlyList<Vector3> LitCells => _litCells;

    /// <summary>
    /// Registers a kiln structure (or refreshes the facing and anchor id of one already known),
    /// so a converted kiln is tracked from the frame it went up.
    /// </summary>
    public void Ensure(Vector3 anchor, int facing, byte anchorId)
    {
        var key = KeyOf(anchor);
        if (!_kilns.TryGetValue(key, out var state))
        {
            state = new KilnState();
            _kilns[key] = state;
        }
        state.Facing = facing;
        state.AnchorId = anchorId;
        state.Profile = ProfileOf(anchorId);
    }

    /// <summary>The kiln profile of the block <paramref name="anchorId"/>, or
    /// <see cref="KilnProfile.Default"/> when that block is not (or no longer) a kiln.</summary>
    public static KilnProfile ProfileOf(byte anchorId) => BlockType.ById[anchorId]?.Kiln ?? KilnProfile.Default;

    /// <summary>Live state of the kiln anchored at this cell, or null when none is tracked.</summary>
    public KilnState? StateAt(int x, int y, int z) => _kilns.GetValueOrDefault((x, y, z));

    /// <summary>
    /// Puts one unit of <paramref name="fuel"/> into a tracked kiln. Refuses anything that is not
    /// a fuel, a full hearth, and a second kind of fuel while another is already stored.
    /// </summary>
    public bool TryAddFuel(Vector3 anchor, Item fuel)
    {
        if (fuel.FuelSeconds <= 0f) return false;
        if (!_kilns.TryGetValue(KeyOf(anchor), out var state)) return false;
        if (state.Fuel >= state.Profile.FuelCap) return false;
        if (state.FuelItemId != 0 && state.FuelItemId != fuel.Id) return false;

        state.FuelItemId = fuel.Id;
        state.FuelSeconds = fuel.FuelSeconds;
        state.Fuel++;
        return true;
    }

    /// <summary>
    /// Whether a crucible holding <paramref name="contents"/> can take one more charge of
    /// <paramref name="recipe"/> in a kiln that may (<paramref name="canAlloy"/>) or may not alloy:
    /// the metal has to mix with what the vessel holds (<see cref="AlloyRules.CanMix"/> over
    /// <see cref="ContentRegistry.AlloyRecipes"/>: one metal in a plain kiln, the metals of one
    /// alloy in an alloy kiln) and the charge has to fit inside <paramref name="capacityMl"/>,
    /// every metal, pending and molten, counted together.
    /// </summary>
    public static bool CanAccept(CrucibleComponent contents, MeltRecipe recipe, int capacityMl, bool canAlloy) =>
        AlloyRules.CanMix(contents, recipe.MaterialId, canAlloy, ContentRegistry.AlloyRecipes)
        && contents.TotalMl + recipe.AmountMl <= capacityMl;

    /// <summary>The metal this kiln's next melt chunk melts, or 0 when nothing may melt
    /// (<see cref="AlloyRules.NextToMelt"/>).</summary>
    public static byte MeltingMaterial(KilnState state) =>
        state.HasCrucible
            ? AlloyRules.NextToMelt(state.Contents, state.Profile.CanAlloy, ContentRegistry.AlloyRecipes)
            : (byte)0;

    /// <summary>Pending ml of the metal currently melting; what one melt chunk is taken from.</summary>
    private static int MeltingPendingMl(KilnState state) => state.Contents.PendingOf(MeltingMaterial(state));

    /// <summary>The molten material the drawn pool is tinted as: the metal with the most molten ml,
    /// else the one melting into an empty pool, else 0.</summary>
    public static byte PoolMaterial(KilnState state) =>
        state.Contents.MostMoltenMaterial is var molten and not 0 ? molten : MeltingMaterial(state);

    /// <summary>How much the crucible in this kiln holds; 0 when the slot is empty.</summary>
    public static int CrucibleCapacityOf(KilnState state) =>
        ItemRegistry.TryGet(state.CrucibleItemId)?.CrucibleCapacityMl ?? 0;

    /// <summary>
    /// How far through the chunk currently melting this kiln is, 0..1 — what makes the rendered
    /// pool rise continuously between the 100 ml bookkeeping steps. Deliberately carries no
    /// <see cref="KilnState.Lit"/> term: the flag flips off the frame the last unit burns out,
    /// and gating on it would drop the drawn surface by a whole chunk at burn-out and snap it
    /// back on relight. A kiln that was never lit still reads 0, because
    /// <see cref="KilnState.MeltLeft"/> sits at a whole <see cref="KilnProfile.MeltSeconds"/> until
    /// the first lit tick.
    /// </summary>
    public static float MeltProgress01(KilnState state) =>
        MeltingMaterial(state) != 0
            ? 1f - MathUtils.Clamp01(state.MeltLeft / state.Profile.MeltSeconds)
            : 0f;

    /// <summary>
    /// Milliliters of molten metal the render pass should draw: the settled pool (every metal)
    /// plus the fraction of the chunk currently melting. Continuous across a melt tick (the 100 ml
    /// the bookkeeping moves is exactly the fraction this was already showing) and across burn-out.
    /// </summary>
    public static float DisplayMoltenMl(KilnState state) =>
        state.Contents.MoltenMl
        + MeltProgress01(state) * Math.Min(MeltChunkMl, MeltingPendingMl(state));

    /// <summary>
    /// Milliliters of solid charge the render pass should still show waiting over the pool: the
    /// exact complement of <see cref="DisplayMoltenMl"/>, so drawn pending plus drawn molten
    /// always equals <see cref="CrucibleComponent.TotalMl"/> and the lump shrinks by precisely
    /// what the pool gains.
    /// </summary>
    public static float DisplayPendingMl(KilnState state) =>
        state.Contents.PendingMl
        - MeltProgress01(state) * Math.Min(MeltChunkMl, MeltingPendingMl(state));

    /// <summary>
    /// Milliliters one unit of the last charged input is worth — the size at which the drawn lump
    /// reads as one whole piece. Falls back to a single melt chunk when no input was recorded, so
    /// the lump's scale stays finite whatever the vessel remembers.
    /// </summary>
    public static int PendingUnitMl(KilnState state) =>
        ContentRegistry.MeltRecipeForInput(state.Contents.PendingInputId)?.AmountMl ?? MeltChunkMl;

    /// <summary>
    /// Stands a crucible in the hearth, with whatever the vessel was carrying. Refuses an
    /// untracked kiln and one that already holds a crucible. The melt timer restarts here: the
    /// item's stack carries no timer of its own, so taking a vessel out mid-chunk and putting it
    /// straight back re-melts that partial chunk.
    /// </summary>
    public bool TryPlaceCrucible(Vector3 anchor, byte itemId, CrucibleComponent contents)
    {
        if (!_kilns.TryGetValue(KeyOf(anchor), out var state)) return false;
        if (state.HasCrucible) return false;

        state.HasCrucible = true;
        state.CrucibleItemId = itemId;
        state.Contents = contents;
        state.MeltLeft = state.Profile.MeltSeconds;
        return true;
    }

    /// <summary>Lifts the crucible back out, contents and all, and empties the slot.</summary>
    public bool TryTakeCrucible(Vector3 anchor, out byte itemId, out CrucibleComponent contents)
    {
        itemId = 0;
        contents = default;
        if (!_kilns.TryGetValue(KeyOf(anchor), out var state) || !state.HasCrucible) return false;

        itemId = state.CrucibleItemId;
        contents = state.Contents;
        state.HasCrucible = false;
        state.CrucibleItemId = 0;
        state.Contents = default;
        return true;
    }

    /// <summary>
    /// Charges the crucible with one unit of <paramref name="inputId"/>, as pending metal a fire
    /// still has to melt. Refuses anything with no melt recipe, a kiln with no crucible, and a
    /// vessel whose metals the charge may not join (<see cref="CanAccept"/>) or with no room.
    /// Needs no fire — loading is cold work, and so is nothing else: alloying waits for the fire.
    /// </summary>
    public bool TryInsertMelt(Vector3 anchor, byte inputId)
    {
        var recipe = ContentRegistry.MeltRecipeForInput(inputId);
        if (recipe == null) return false;
        if (!_kilns.TryGetValue(KeyOf(anchor), out var state) || !state.HasCrucible) return false;
        if (!CanAccept(state.Contents, recipe, CrucibleCapacityOf(state), state.Profile.CanAlloy)) return false;

        state.Contents = state.Contents.With(recipe.MaterialId, 0, recipe.AmountMl) with { PendingInputId = inputId };
        return true;
    }

    /// <summary>Whether a strike would light this kiln: tracked, unlit and holding fuel.</summary>
    public bool CanIgnite(Vector3 anchor) =>
        _kilns.TryGetValue(KeyOf(anchor), out var state) && !state.Lit && state.Fuel > 0;

    /// <summary>Lights the kiln, spending the first unit of its fuel right away.</summary>
    public bool Ignite(Vector3 anchor)
    {
        if (!CanIgnite(anchor)) return false;

        var state = _kilns[KeyOf(anchor)];
        state.Fuel--;
        state.Lit = true;
        state.BurnLeft = state.FuelSeconds;
        RefreshCells();
        return true;
    }

    /// <summary>
    /// Drops a broken kiln's unburnt fuel at <paramref name="dropCell"/> (the mined cell — never
    /// a point in front of the opening, which can sit inside a wall) and forgets the structure.
    /// </summary>
    public void NotifyBlockRemoved(Vector3 anchor, Vector3 dropCell, DropManager? drops)
    {
        var key = KeyOf(anchor);
        if (!_kilns.TryGetValue(key, out var state)) return;
        _kilns.Remove(key);
        SpawnFuel(state, dropCell, drops);
        SpawnCrucible(state, dropCell, drops);
        RefreshCells();
    }

    /// <summary>Every tracked kiln, for the casting run's render pass.</summary>
    public IEnumerable<(Vector3 Anchor, int Facing, KilnState State)> KilnView()
    {
        foreach (var (key, state) in _kilns)
            yield return (new Vector3(key.X, key.Y, key.Z), state.Facing, state);
    }

    /// <summary>
    /// Burns down lit kilns and sweeps broken ones. A structure whose anchor cell no longer holds
    /// its anchor block, or that lost any cell of its volume, is reverted to the block it was
    /// built from, drops its fuel and is forgotten — that is the only cleanup a blast gets, since
    /// <see cref="TntManager"/> clears cells without knowing about structures. A kiln with a cell
    /// in an unloaded chunk freezes instead, so a half-loaded structure is never mangled.
    /// <paramref name="lod"/> receives the surface change of each reverted cell (see
    /// <see cref="LodSurface.RecordEdit"/>); null records nothing, which the headless tests use.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod = null)
    {
        if (deltaTime <= 0f || _kilns.Count == 0) return;
        _stale.Clear();
        var changed = false;

        foreach (var (key, state) in _kilns)
        {
            var anchor = new Vector3(key.X, key.Y, key.Z);
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;

            var anchorType = BlockType.ById[state.AnchorId];
            if (anchorType == null)
            {
                _stale.Add(key);
                continue;
            }

            // The anchor cell itself is gone or was overwritten: revert whatever parts survive
            var anchorHeld = chunks.TryGetCell(key.X, key.Y, key.Z, out var anchorCellId, out _)
                && anchorCellId == state.AnchorId;
            if (!anchorHeld)
            {
                _stale.Add(key);
                continue;
            }

            // A part was blasted out; an unloaded cell is not a verdict, so freeze on that
            if (!Multiblock.IsIntact(chunks, anchor, state.Facing, anchorType, out var anyUnloaded)
                && !anyUnloaded)
            {
                _stale.Add(key);
                continue;
            }

            if (state.Lit && state.HasCrucible)
                Melt(state, deltaTime);

            if (!state.Lit) continue;
            state.BurnLeft -= deltaTime;
            while (state.BurnLeft <= 0f)
            {
                if (state.Fuel > 0)
                {
                    state.Fuel--;
                    state.BurnLeft += state.FuelSeconds;
                }
                else
                {
                    state.Lit = false;
                    state.BurnLeft = 0f;
                    changed = true;
                    break;
                }
            }
        }

        foreach (var key in _stale)
        {
            if (!_kilns.TryGetValue(key, out var state)) continue;
            _kilns.Remove(key);
            changed = true;

            var anchor = new Vector3(key.X, key.Y, key.Z);
            RevertStructure(chunks, lod, key, state.AnchorId, anchor);
            SpawnFuel(state, anchor, drops);
            SpawnCrucible(state, anchor, drops);
        }

        if (changed)
            RefreshCells();
    }

    /// <summary>
    /// One lit kiln's melt step. Runs off the pre-burn <see cref="KilnState.Lit"/> flag, so the
    /// frame the fuel runs out still melts. An alloy kiln first turns every full set of an alloy's
    /// parts into the alloy (<see cref="AlloyRules.Resolve"/>), whose charge then melts like any
    /// other. The timer carries its overshoot, which is what keeps <see cref="MeltProgress01"/>
    /// continuous across a tick and lets one big dt melt several chunks.
    /// </summary>
    private static void Melt(KilnState state, float deltaTime)
    {
        if (state.Profile.CanAlloy)
            state.Contents = AlloyRules.Resolve(state.Contents, ContentRegistry.AlloyRecipes);

        var melting = MeltingMaterial(state);
        if (melting != 0)
        {
            state.MeltLeft -= deltaTime;
            while (state.MeltLeft <= 0f && melting != 0)
            {
                var melted = Math.Min(MeltChunkMl, state.Contents.PendingOf(melting));
                state.Contents = state.Contents.With(melting, melted, -melted);
                state.MeltLeft += state.Profile.MeltSeconds;
                melting = MeltingMaterial(state);
            }
        }
        // Nothing left to melt: park a full chunk so the next charge starts from scratch
        if (melting == 0)
            state.MeltLeft = state.Profile.MeltSeconds;
    }

    /// <summary>
    /// Puts a broken kiln's cells back to its source block and re-tiles the far LOD there.
    /// The anchor's own block must still resolve; its fuel and crucible are handed back
    /// separately by the caller.
    /// </summary>
    private static void RevertStructure(ChunkManager chunks, LodManager? lod, (int X, int Y, int Z) key,
        byte anchorId, Vector3 anchor)
    {
        if (BlockType.ById[anchorId] is not { } anchorType) return;

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Revert(chunks, anchor, anchorType, null, touchedChunks);
        foreach (var chunkPos in touchedChunks)
            if (chunks.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(chunks);

        // The cells went back to their source block, which can re-tile the far LOD
        if (lod == null) return;
        foreach (var (dx, dy, dz) in Multiblock.Offsets(anchorType.MultiblockSize))
        {
            int cx = key.X + dx, cy = key.Y + dy, cz = key.Z + dz;
            if (LodSurface.RecordEdit(chunks, cx, cy, cz))
                lod.MarkEditedColumn(cx, cz);
        }
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 anchor) => VoxelCoords.BlockCellOf(anchor);

    private static void SpawnFuel(KilnState state, Vector3 cell, DropManager? drops)
    {
        if (drops == null || state.Fuel <= 0 || state.FuelItemId == 0) return;
        for (var i = 0; i < state.Fuel; i++)
            drops.Spawn(state.FuelItemId, cell);
        state.Fuel = 0;
    }

    /// <summary>
    /// Drops a broken kiln's crucible at <paramref name="cell"/> and empties the slot. What was
    /// inside it is lost: the vessel drops as a plain item and the drop carries no component.
    /// </summary>
    private static void SpawnCrucible(KilnState state, Vector3 cell, DropManager? drops)
    {
        if (!state.HasCrucible) return;
        if (state.CrucibleItemId != 0)
            drops?.Spawn(state.CrucibleItemId, cell);
        state.HasCrucible = false;
        state.CrucibleItemId = 0;
        state.Contents = default;
    }

    private void RefreshCells()
    {
        _litCells.Clear();
        foreach (var (key, state) in _kilns)
        {
            if (!state.Lit) continue;
            var anchor = new Vector3(key.X, key.Y, key.Z);
            var layout = state.Profile.Layout;
            _litCells.Add(layout.ToWorld(anchor, state.Facing, layout.FireLocal)
                - FireParticleSystem.SpawnOffset);
        }
    }

    /// <summary>Every tracked kiln by content name, for <c>stations.json</c> (at most
    /// <see cref="StationNetCodec.MaxEntries"/>). A kiln whose anchor block has no save name is
    /// skipped.</summary>
    internal List<StationSaveData.KilnData> Capture()
    {
        var result = new List<StationSaveData.KilnData>();
        foreach (var ((x, y, z), state) in _kilns)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (SaveNames.NameOf(state.AnchorId) is not { } anchor) continue;
            result.Add(new StationSaveData.KilnData
            {
                Cell = [x, y, z],
                Anchor = anchor,
                Facing = state.Facing,
                Fuel = state.Fuel,
                FuelItem = SaveNames.NameOf(state.FuelItemId),
                FuelSeconds = state.FuelSeconds,
                Lit = state.Lit,
                BurnLeft = state.BurnLeft,
                Crucible = state.HasCrucible ? SaveNames.NameOf(state.CrucibleItemId) : null,
                Contents = state.HasCrucible && !state.Contents.IsEmpty ? CrucibleSaveCodec.Export(state.Contents) : null,
                MeltLeft = state.MeltLeft,
            });
        }
        return result;
    }

    /// <summary>Adds the kilns saved by <see cref="Capture"/> to an empty registry. A kiln whose
    /// anchor no longer resolves, or whose numbers are out of range, is dropped; a fuel, crucible or
    /// metal that no longer resolves is dropped alone, and a crucible's amounts are clamped to its
    /// capacity (<see cref="CrucibleSaveCodec.Import"/>). Returns how many things were dropped.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.KilnData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key)
                || !SaveNames.TryResolve(data.Anchor, out var anchor)
                || data.Facing is < 0 or > 3 || data.Fuel < 0 || data.Fuel > ProfileOf(anchor).FuelCap
                || !float.IsFinite(data.FuelSeconds) || !float.IsFinite(data.BurnLeft)
                || !float.IsFinite(data.MeltLeft))
            {
                skipped++;
                continue;
            }
            var state = new KilnState
            {
                AnchorId = anchor,
                Profile = ProfileOf(anchor),
                Facing = data.Facing,
                Fuel = data.Fuel,
                FuelSeconds = data.FuelSeconds,
                Lit = data.Lit,
                BurnLeft = data.BurnLeft,
                MeltLeft = data.MeltLeft,
            };
            if (data.Fuel > 0 || data.FuelItem != null)
            {
                if (SaveNames.TryResolve(data.FuelItem, out var fuel))
                    state.FuelItemId = fuel;
                else
                {
                    state.Fuel = 0;
                    skipped++;
                }
            }
            if (data.Crucible != null)
            {
                if (SaveNames.TryResolve(data.Crucible, out var crucible))
                {
                    state.HasCrucible = true;
                    state.CrucibleItemId = crucible;
                    var dropped = new List<string>();
                    state.Contents = CrucibleSaveCodec.Import(data.Contents, CrucibleCapacityOf(state), dropped);
                    skipped += dropped.Count;
                }
                else
                {
                    skipped++;
                }
            }
            _kilns[key] = state;
        }
        RefreshCells();
        return skipped;
    }

    /// <summary>Writes every tracked kiln (fuel, fire, crucible and melt), for the station
    /// broadcast (<see cref="StationNetCodec"/>).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        // At most what a client accepts (StationNetCodec.MaxEntries)
        var leftKilns = Math.Min(_kilns.Count, StationNetCodec.MaxEntries);
        writer.Write(leftKilns);
        foreach (var ((x, y, z), state) in _kilns)
        {
            if (leftKilns-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write(state.AnchorId);
            writer.Write((byte)state.Facing);
            writer.Write((byte)state.Fuel);
            writer.Write(state.FuelItemId);
            writer.Write(state.FuelSeconds);
            writer.Write(state.Lit);
            writer.Write(state.BurnLeft);
            writer.Write(state.HasCrucible);
            writer.Write(state.CrucibleItemId);
            StationNetCodec.WriteCrucible(writer, state.Contents);
            writer.Write(state.MeltLeft);
        }
    }

    /// <summary>
    /// Replaces the tracked kilns with a snapshot written by <see cref="WriteNet"/>: a multiplayer
    /// client's display-only mirror, which never runs <see cref="Update"/> (its integrity sweep
    /// would revert cells only the server may write).
    /// </summary>
    internal void ReadNet(PacketReader reader)
    {
        _kilns.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var state = new KilnState
            {
                AnchorId = reader.ReadByte(),
                Facing = reader.ReadByte(),
                Fuel = reader.ReadByte(),
                FuelItemId = reader.ReadByte(),
                FuelSeconds = StationNetCodec.ReadFinite(reader),
                Lit = reader.ReadBool(),
                BurnLeft = StationNetCodec.ReadFinite(reader),
                HasCrucible = reader.ReadBool(),
                CrucibleItemId = reader.ReadByte(),
            };
            state.Contents = StationNetCodec.ReadCrucible(reader);
            state.MeltLeft = StationNetCodec.ReadFinite(reader);
            state.Profile = ProfileOf(state.AnchorId);
            if (state.Facing > 3 || state.Fuel > state.Profile.FuelCap)
                throw new System.IO.InvalidDataException("kiln snapshot out of range");
            _kilns[key] = state;
        }
        RefreshCells();
    }
}
