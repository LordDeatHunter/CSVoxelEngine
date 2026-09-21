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

    /// <summary>Seconds a lit kiln takes to melt 100 ml inside a crucible. Declared here as the
    /// single source of that pace; the casting run's melt loop is what reads it.</summary>
    public const float MeltSeconds = 2f;

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

        // Crucible slot (MaterialId, MoltenMl, PendingMl) is added by the casting run
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
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
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
            }

            SpawnFuel(state, anchor, drops);
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
