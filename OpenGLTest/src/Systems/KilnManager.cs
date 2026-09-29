using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Block-entity registry for mud brick kilns, keyed by the structure's anchor cell (the volume's
/// minimum corner). A kiln stores up to <see cref="MaxFuel"/> units of one fuel item; striking it
/// with a firestarter burns one unit at once and the next whenever the burn timer runs out, so a
/// kiln with four coal stays lit for four minutes and then goes out.
///
/// <para>
/// A kiln also holds one crucible. Right-clicking a melt input into it charges the vessel with
/// <em>pending</em> metal; a lit kiln then turns <see cref="MeltChunkMl"/> of that pending charge
/// molten every <see cref="MeltSeconds"/>. Melting only <em>pauses</em> when the fire goes out or
/// the vessel is taken — <see cref="KilnState.MeltLeft"/> simply stops ticking — so relighting
/// resumes the partial chunk instead of starting it over. <see cref="MeltProgress01"/> and
/// <see cref="DisplayMoltenMl"/> are what the render pass reads, and neither depends on
/// <see cref="KilnState.Lit"/>, so the pool never drops when the last unit burns out.
/// </para>
///
/// <para>
/// The per-frame <see cref="Update"/> doubles as the structure's integrity sweep: nothing but
/// mining goes through <c>World.RemoveBlock</c>, so a blast that takes a cell (a part
/// <em>or</em> the anchor) would otherwise leave the rest standing as invisible solid blocks.
/// <see cref="KilnState.AnchorId"/> is what lets the sweep revert a kiln whose anchor cell is
/// already gone. Only kilns get this: a future multiblock that is not a kiln would need its own
/// registry, or a generic one.
/// </para>
///
/// Main thread only, like <see cref="QuernManager"/> and <see cref="CampfireManager"/>, and
/// in-memory only — fuel and lit state do not survive a restart.
/// </summary>
public sealed class KilnManager
{
    /// <summary>Units of fuel a kiln holds at once.</summary>
    public const int MaxFuel = 4;

    /// <summary>Seconds a lit kiln takes to melt one <see cref="MeltChunkMl"/> chunk of a
    /// crucible's pending charge. Declared here as the single source of that pace.</summary>
    public const float MeltSeconds = 2f;

    /// <summary>Millilitres that turn molten per <see cref="MeltSeconds"/> while the kiln
    /// burns.</summary>
    public const int MeltChunkMl = 100;

    /// <summary>Live state of one kiln structure.</summary>
    public sealed class KilnState
    {
        /// <summary>Block id of the anchor this kiln was registered with. Remembered so the
        /// integrity sweep can revert the structure even when the anchor cell itself is gone.</summary>
        public byte AnchorId;

        /// <summary>Which way the opening points (engine face index 0..3).</summary>
        public int Facing;

        /// <summary>Unburnt units waiting in the hearth, 0..<see cref="MaxFuel"/>.</summary>
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
    /// adds that offset back) spawns exactly at <see cref="KilnRenderer.FireLocal"/> and the point
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
    }

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
        if (state.Fuel >= MaxFuel) return false;
        if (state.FuelItemId != 0 && state.FuelItemId != fuel.Id) return false;

        state.FuelItemId = fuel.Id;
        state.FuelSeconds = fuel.FuelSeconds;
        state.Fuel++;
        return true;
    }

    /// <summary>
    /// Whether a crucible holding <paramref name="contents"/> can take one more charge of
    /// <paramref name="recipe"/>: the material has to match (or the vessel be empty, whatever
    /// stale material id it still remembers) and the charge has to fit inside
    /// <paramref name="capacityMl"/>, pending and molten counted together.
    /// </summary>
    public static bool CanAccept(CrucibleComponent contents, MeltRecipe recipe, int capacityMl) =>
        (contents.IsEmpty || contents.MaterialId == recipe.MaterialId)
        && contents.TotalMl + recipe.AmountMl <= capacityMl;

    /// <summary>How much the crucible in this kiln holds; 0 when the slot is empty.</summary>
    public static int CrucibleCapacityOf(KilnState state) =>
        ItemRegistry.TryGet(state.CrucibleItemId)?.CrucibleCapacityMl ?? 0;

    /// <summary>
    /// How far through the chunk currently melting this kiln is, 0..1 — what makes the rendered
    /// pool rise continuously between the 100 ml bookkeeping steps. Deliberately carries no
    /// <see cref="KilnState.Lit"/> term: the flag flips off the frame the last unit burns out,
    /// and gating on it would drop the drawn surface by a whole chunk at burn-out and snap it
    /// back on relight. A kiln that was never lit still reads 0, because
    /// <see cref="KilnState.MeltLeft"/> sits at <see cref="MeltSeconds"/> until the first lit tick.
    /// </summary>
    public static float MeltProgress01(KilnState state) =>
        state.HasCrucible && state.Contents.PendingMl > 0
            ? 1f - MathUtils.Clamp01(state.MeltLeft / MeltSeconds)
            : 0f;

    /// <summary>
    /// Millilitres of molten metal the render pass should draw: the settled pool plus the
    /// fraction of the chunk currently melting. Continuous across a melt tick (the 100 ml the
    /// bookkeeping moves is exactly the fraction this was already showing) and across burn-out.
    /// </summary>
    public static float DisplayMoltenMl(KilnState state) =>
        state.Contents.MoltenMl
        + MeltProgress01(state) * Math.Min(MeltChunkMl, state.Contents.PendingMl);

    /// <summary>
    /// Millilitres of solid charge the render pass should still show waiting over the pool: the
    /// exact complement of <see cref="DisplayMoltenMl"/>, so drawn pending plus drawn molten
    /// always equals <see cref="CrucibleComponent.TotalMl"/> and the lump shrinks by precisely
    /// what the pool gains.
    /// </summary>
    public static float DisplayPendingMl(KilnState state) =>
        state.Contents.PendingMl
        - MeltProgress01(state) * Math.Min(MeltChunkMl, state.Contents.PendingMl);

    /// <summary>
    /// Millilitres one unit of the last charged input is worth — the size at which the drawn lump
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
        state.MeltLeft = MeltSeconds;
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
    /// vessel with the wrong material or no room. Needs no fire — loading is cold work.
    /// </summary>
    public bool TryInsertMelt(Vector3 anchor, byte inputId)
    {
        var recipe = ContentRegistry.MeltRecipeForInput(inputId);
        if (recipe == null) return false;
        if (!_kilns.TryGetValue(KeyOf(anchor), out var state) || !state.HasCrucible) return false;
        if (!CanAccept(state.Contents, recipe, CrucibleCapacityOf(state))) return false;

        state.Contents = state.Contents with
        {
            MaterialId = recipe.MaterialId,
            PendingMl = state.Contents.PendingMl + recipe.AmountMl,
            PendingInputId = inputId,
        };
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
    /// its anchor block, or that lost any of its eight cells, is reverted to the block it was
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

            // Melting runs off the pre-burn Lit flag, so the frame the fuel runs out still melts.
            // The timer carries its overshoot, which is what keeps MeltProgress01 continuous
            // across a tick and lets one big dt melt several chunks.
            if (state.Lit && state.HasCrucible && state.Contents.PendingMl > 0)
            {
                state.MeltLeft -= deltaTime;
                while (state.MeltLeft <= 0f && state.Contents.PendingMl > 0)
                {
                    var melted = Math.Min(MeltChunkMl, state.Contents.PendingMl);
                    state.Contents = state.Contents with
                    {
                        MoltenMl = state.Contents.MoltenMl + melted,
                        PendingMl = state.Contents.PendingMl - melted,
                    };
                    state.MeltLeft += MeltSeconds;
                }
                // Nothing left to melt: park a full chunk so the next charge starts from scratch
                if (state.Contents.PendingMl == 0)
                    state.MeltLeft = MeltSeconds;
            }

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
            if (BlockType.ById[state.AnchorId] is { } anchorType)
            {
                var touchedChunks = new HashSet<Vector3>();
                Multiblock.Revert(chunks, anchor, anchorType, null, touchedChunks);
                foreach (var chunkPos in touchedChunks)
                {
                    if (chunks.Chunks.TryGetValue(chunkPos, out var touched))
                        touched.RemeshAfterBatchEdit(chunks);
                }

                // The cells went back to their source block, which can re-tile the far LOD
                if (lod != null)
                {
                    foreach (var (dx, dy, dz) in Multiblock.Offsets)
                    {
                        int cx = key.X + dx, cy = key.Y + dy, cz = key.Z + dz;
                        if (LodSurface.RecordEdit(chunks, cx, cy, cz))
                            lod.MarkEditedColumn(cx, cz);
                    }
                }
            }

            SpawnFuel(state, anchor, drops);
            SpawnCrucible(state, anchor, drops);
        }

        if (changed)
            RefreshCells();
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
            _litCells.Add(KilnRenderer.ToWorld(anchor, state.Facing, KilnRenderer.FireLocal)
                - FireParticleSystem.SpawnOffset);
        }
    }
}
