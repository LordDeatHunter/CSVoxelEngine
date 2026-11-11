using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Stations;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// Block-entity registry for bloomeries, keyed by the structure's anchor cell (the volume's minimum corner): a
/// solid-state iron smelter. Crushed ore (the profile's <see cref="BloomeryProfile.OreId"/>) and fuel are loaded by
/// right-click; a firestarter lights it once it holds both. A lit bloomery cools every second
/// (<see cref="BloomeryRules.Decay"/>) and only the player's bellows keeps it hot: each jump within
/// <see cref="BloomeryConfig.PumpRangeBlocks"/> of it (<see cref="Pump"/>) adds heat. Ore smelts while the heat is
/// at or over <see cref="BloomeryConfig.BandMin"/>; a unit that spent too long over <see cref="BloomeryConfig.BandMax"/>
/// burns to slag, a clean one becomes a bloom. Blooms and slag wait in the shaft until an empty hand collects them,
/// and no new ore goes in before then. Iron never melts here, so none of this touches the crucible pipeline.
///
/// <para>
/// Like <see cref="KilnManager"/>, <see cref="Update"/> doubles as the structure's integrity sweep (a blast that takes
/// a cell reverts the rest), and the whole state is saved (<see cref="Capture"/>, <see cref="Restore"/>) and mirrored
/// to clients (<see cref="WriteNet"/>, <see cref="ReadNet"/>). Main thread only.
/// </para>
/// </summary>
public sealed class BloomeryManager
{
    /// <summary>Live state of one bloomery structure.</summary>
    public sealed class BloomeryState
    {
        /// <summary>Block id of the anchor this bloomery was registered with; remembered so the sweep can revert
        /// the structure even when the anchor cell itself is gone.</summary>
        public ushort AnchorId;

        /// <summary>How this bloomery behaves, derived from <see cref="AnchorId"/> (<see cref="ProfileOf"/>); never
        /// saved or sent.</summary>
        public BloomeryProfile Profile = BloomeryProfile.Default;

        /// <summary>Which way the opening points (engine face index 0..3).</summary>
        public int Facing;

        /// <summary>Units of ore waiting to smelt (including the one smelting now).</summary>
        public int Ore;

        /// <summary>Unburnt fuel units waiting in the shaft.</summary>
        public int Fuel;

        /// <summary>Item id of the stored fuel; 0 when there is none.</summary>
        public ushort FuelItemId;

        /// <summary>Seconds one stored unit burns for.</summary>
        public float FuelSeconds;

        /// <summary>Whether the fire is burning.</summary>
        public bool Lit;

        /// <summary>Seconds left on the fuel unit burning now.</summary>
        public float BurnLeft;

        /// <summary>The fire's heat, 0..1 (0 while unlit).</summary>
        public float Heat;

        /// <summary>Seconds the unit smelting now has progressed, 0..<see cref="BloomeryConfig.SecondsPerOre"/>.</summary>
        public float UnitProgress;

        /// <summary>Seconds the unit smelting now spent over the band.</summary>
        public float UnitOverheat;

        /// <summary>Finished blooms waiting to be collected.</summary>
        public int Blooms;

        /// <summary>Slag waiting to be collected.</summary>
        public int Slag;

        /// <summary>Seconds until the next pump counts (server and singleplayer only; not saved or sent).</summary>
        public float PumpCooldown;
    }

    private readonly Dictionary<(int X, int Y, int Z), BloomeryState> _bloomeries = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();
    private readonly List<Vector3> _litCells = new();

    /// <summary>Whether any bloomery is tracked, for render early-out and the empty-snapshot rule.</summary>
    public bool HasAny => _bloomeries.Count > 0;

    /// <summary>Particle and light origins of every lit bloomery, in the form <see cref="KilnManager.LitCells"/> uses.</summary>
    public IReadOnlyList<Vector3> LitCells => _litCells;

    /// <summary>Registers a bloomery structure (or refreshes the facing and anchor id of one already known).</summary>
    public void Ensure(Vector3 anchor, int facing, ushort anchorId)
    {
        var key = KeyOf(anchor);
        if (!_bloomeries.TryGetValue(key, out var state))
        {
            state = new BloomeryState();
            _bloomeries[key] = state;
        }
        state.Facing = facing;
        state.AnchorId = anchorId;
        state.Profile = ProfileOf(anchorId);
    }

    /// <summary>The bloomery profile of the block <paramref name="anchorId"/>, or <see cref="BloomeryProfile.Default"/>
    /// when that block is not (or no longer) a bloomery.</summary>
    public static BloomeryProfile ProfileOf(ushort anchorId) =>
        BlockType.ById[anchorId]?.Bloomery ?? BloomeryProfile.Default;

    /// <summary>Live state of the bloomery anchored at this cell, or null when none is tracked.</summary>
    public BloomeryState? StateAt(int x, int y, int z) => _bloomeries.GetValueOrDefault((x, y, z));

    /// <summary>Every tracked bloomery, for the render pass and the look-at readout.</summary>
    public IEnumerable<(Vector3 Anchor, int Facing, BloomeryState State)> View()
    {
        foreach (var (key, state) in _bloomeries)
            yield return (new Vector3(key.X, key.Y, key.Z), state.Facing, state);
    }

    /// <summary>
    /// Loads one unit of <paramref name="item"/> as ore. Refuses anything but the profile's ore, a full shaft, and
    /// any load while the last run's output is still inside (collect it first).
    /// </summary>
    public bool TryAddOre(Vector3 anchor, ushort item)
    {
        if (!_bloomeries.TryGetValue(KeyOf(anchor), out var state)) return false;
        if (item == 0 || item != state.Profile.OreId) return false;
        if (state.Ore >= state.Profile.OreCap || state.Blooms + state.Slag > 0) return false;
        state.Ore++;
        return true;
    }

    /// <summary>Puts one unit of <paramref name="fuel"/> into the shaft. Refuses anything that is not a fuel, a full
    /// shaft, and a second kind of fuel while another is stored.</summary>
    public bool TryAddFuel(Vector3 anchor, Item fuel)
    {
        if (fuel.FuelSeconds <= 0f) return false;
        if (!_bloomeries.TryGetValue(KeyOf(anchor), out var state)) return false;
        if (state.Fuel >= state.Profile.FuelCap) return false;
        if (state.FuelItemId != 0 && state.FuelItemId != fuel.Id) return false;

        state.FuelItemId = fuel.Id;
        state.FuelSeconds = fuel.FuelSeconds;
        state.Fuel++;
        return true;
    }

    /// <summary>Whether a strike would light this bloomery: tracked, unlit, with ore and fuel inside.</summary>
    public bool CanIgnite(Vector3 anchor) =>
        _bloomeries.TryGetValue(KeyOf(anchor), out var state) && !state.Lit && state.Fuel > 0 && state.Ore > 0;

    /// <summary>Lights the bloomery, spending the first unit of its fuel right away, at
    /// <see cref="BloomeryConfig.IgnitionHeat"/>.</summary>
    public bool Ignite(Vector3 anchor)
    {
        if (!CanIgnite(anchor)) return false;

        var state = _bloomeries[KeyOf(anchor)];
        state.Fuel--;
        state.Lit = true;
        state.BurnLeft = state.FuelSeconds;
        state.Heat = BloomeryConfig.IgnitionHeat;
        RefreshCells();
        return true;
    }

    /// <summary>
    /// Hands over what a finished run left (blooms, then slag) and empties the shaft of it. The ids are the
    /// profile's; the caller puts them in the player's inventory or on the ground. False when nothing is waiting.
    /// </summary>
    public bool TryCollect(Vector3 anchor, List<ushort> output)
    {
        if (!_bloomeries.TryGetValue(KeyOf(anchor), out var state) || state.Blooms + state.Slag == 0) return false;
        for (var i = 0; i < state.Blooms; i++)
            output.Add(state.Profile.BloomId);
        for (var i = 0; i < state.Slag; i++)
            output.Add(state.Profile.SlagId);
        state.Blooms = 0;
        state.Slag = 0;
        return true;
    }

    /// <summary>
    /// One bellows pump from a body at <paramref name="position"/> (a jump): every lit bloomery whose center lies
    /// within <see cref="BloomeryConfig.PumpRangeBlocks"/> and whose pump cooldown has run out gains a pump of
    /// heat. Authoritative simulation only (singleplayer, and the server for each player's jump). Returns whether
    /// any bloomery took it.
    /// </summary>
    public bool Pump(Vector3 position)
    {
        var pumped = false;
        foreach (var (key, state) in _bloomeries)
        {
            if (!state.Lit || state.PumpCooldown > 0f) continue;
            var half = (BlockType.ById[state.AnchorId]?.MultiblockSize ?? 2) * 0.5f;
            var center = new Vector3(key.X + half, key.Y + half, key.Z + half);
            if (Vector3.DistanceSquared(position, center) > BloomeryConfig.PumpRangeBlocks * BloomeryConfig.PumpRangeBlocks)
                continue;
            state.Heat = BloomeryRules.Pump(state.Heat);
            state.PumpCooldown = BloomeryConfig.PumpCooldownSeconds;
            pumped = true;
        }
        return pumped;
    }

    /// <summary>Drops a broken bloomery's ore, fuel and output at <paramref name="dropCell"/> (the mined cell) and
    /// forgets the structure.</summary>
    public void NotifyBlockRemoved(Vector3 anchor, Vector3 dropCell, DropManager? drops)
    {
        var key = KeyOf(anchor);
        if (!_bloomeries.TryGetValue(key, out var state)) return;
        _bloomeries.Remove(key);
        SpawnContents(state, dropCell, drops);
        RefreshCells();
    }

    /// <summary>
    /// Runs the fires and sweeps broken structures. A structure whose anchor cell no longer holds its anchor block,
    /// or that lost any cell of its volume, is reverted to the block it was built from, drops what it held and is
    /// forgotten (the only cleanup a blast gets); one with a cell in an unloaded chunk freezes instead.
    /// <paramref name="lod"/> receives the surface change of each reverted cell; null records nothing.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod = null)
    {
        if (deltaTime <= 0f || _bloomeries.Count == 0) return;
        _stale.Clear();
        var changed = false;

        foreach (var (key, state) in _bloomeries)
        {
            var anchor = new Vector3(key.X, key.Y, key.Z);
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;

            var anchorType = BlockType.ById[state.AnchorId];
            var anchorHeld = anchorType != null
                && chunks.TryGetCell(key.X, key.Y, key.Z, out var anchorCellId, out _)
                && anchorCellId == state.AnchorId;
            if (!anchorHeld)
            {
                _stale.Add(key);
                continue;
            }

            if (!Multiblock.IsIntact(chunks, anchor, state.Facing, anchorType!, out var anyUnloaded) && !anyUnloaded)
            {
                _stale.Add(key);
                continue;
            }

            changed |= Tick(state, deltaTime);
        }

        foreach (var key in _stale)
        {
            if (!_bloomeries.TryGetValue(key, out var state)) continue;
            _bloomeries.Remove(key);
            changed = true;

            var anchor = new Vector3(key.X, key.Y, key.Z);
            RevertStructure(chunks, lod, key, state.AnchorId, anchor);
            SpawnContents(state, anchor, drops);
        }

        if (changed)
            RefreshCells();
    }

    /// <summary>
    /// One bloomery's fire and smelt for <paramref name="dt"/> seconds. Heat falls, the fuel burns (faster while
    /// overheated), and while the heat is at or over the band the unit in the shaft progresses; a finished unit
    /// becomes a bloom, or slag if it overheated long enough. Returns whether the lit flag changed.
    /// </summary>
    internal static bool Tick(BloomeryState state, float dt)
    {
        state.PumpCooldown = MathF.Max(0f, state.PumpCooldown - dt);
        if (!state.Lit) return false;

        state.Heat = BloomeryRules.Decay(state.Heat, dt);
        var zone = BloomeryRules.ZoneOf(state.Heat);

        if (state.Ore > 0 && BloomeryRules.Smelts(state.Heat))
        {
            state.UnitProgress += dt;
            if (zone == HeatZone.Hot)
                state.UnitOverheat += dt;
            while (state.UnitProgress >= BloomeryConfig.SecondsPerOre && state.Ore > 0)
            {
                state.Ore--;
                if (BloomeryRules.Burns(state.UnitOverheat))
                    state.Slag += BloomeryConfig.SlagPerBurntUnit;
                else
                    state.Blooms++;
                state.UnitProgress -= BloomeryConfig.SecondsPerOre;
                state.UnitOverheat = 0f;
            }
            if (state.Ore == 0)
            {
                state.UnitProgress = 0f;
                state.UnitOverheat = 0f;
            }
        }

        state.BurnLeft -= dt * BloomeryRules.FuelBurnFactor(state.Heat);
        while (state.BurnLeft <= 0f)
        {
            if (state.Fuel > 0)
            {
                state.Fuel--;
                state.BurnLeft += state.FuelSeconds;
                continue;
            }
            state.Lit = false;
            state.BurnLeft = 0f;
            state.Heat = 0f;
            return true;
        }
        return false;
    }

    private static void RevertStructure(ChunkManager chunks, LodManager? lod, (int X, int Y, int Z) key,
        ushort anchorId, Vector3 anchor)
    {
        if (BlockType.ById[anchorId] is not { } anchorType) return;

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Revert(chunks, anchor, anchorType, null, touchedChunks);
        foreach (var chunkPos in touchedChunks)
            if (chunks.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(chunks);

        if (lod == null) return;
        foreach (var (dx, dy, dz) in Multiblock.Offsets(anchorType.MultiblockSize))
        {
            int cx = key.X + dx, cy = key.Y + dy, cz = key.Z + dz;
            if (LodSurface.RecordEdit(chunks, cx, cy, cz))
                lod.MarkEditedColumn(cx, cz);
        }
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 anchor) => VoxelCoords.BlockCellOf(anchor);

    private static void SpawnContents(BloomeryState state, Vector3 cell, DropManager? drops)
    {
        if (drops == null) return;
        for (var i = 0; i < state.Ore; i++)
            drops.Spawn(state.Profile.OreId, cell);
        if (state.FuelItemId != 0)
            for (var i = 0; i < state.Fuel; i++)
                drops.Spawn(state.FuelItemId, cell);
        for (var i = 0; i < state.Blooms; i++)
            drops.Spawn(state.Profile.BloomId, cell);
        for (var i = 0; i < state.Slag; i++)
            drops.Spawn(state.Profile.SlagId, cell);
        state.Ore = 0;
        state.Fuel = 0;
        state.Blooms = 0;
        state.Slag = 0;
    }

    private void RefreshCells()
    {
        _litCells.Clear();
        foreach (var (key, state) in _bloomeries)
        {
            if (!state.Lit) continue;
            var anchor = new Vector3(key.X, key.Y, key.Z);
            // A bloomery wears the mud brick kiln's model, so its fire burns where the kiln's does
            var layout = KilnLayout.Kiln;
            _litCells.Add(layout.ToWorld(anchor, state.Facing, layout.FireLocal) - FireParticleSystem.SpawnOffset);
        }
    }

    /// <summary>Every tracked bloomery by content name, for <c>stations.json</c> (at most
    /// <see cref="StationNetCodec.MaxEntries"/>). One whose anchor block has no save name is skipped.</summary>
    internal List<StationSaveData.BloomeryData> Capture()
    {
        var result = new List<StationSaveData.BloomeryData>();
        foreach (var ((x, y, z), state) in _bloomeries)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (SaveNames.NameOf(state.AnchorId) is not { } anchor) continue;
            result.Add(new StationSaveData.BloomeryData
            {
                Cell = [x, y, z],
                Anchor = anchor,
                Facing = state.Facing,
                Ore = state.Ore,
                Fuel = state.Fuel,
                FuelItem = SaveNames.NameOf(state.FuelItemId),
                FuelSeconds = state.FuelSeconds,
                Lit = state.Lit,
                BurnLeft = state.BurnLeft,
                Heat = state.Heat,
                UnitProgress = state.UnitProgress,
                UnitOverheat = state.UnitOverheat,
                Blooms = state.Blooms,
                Slag = state.Slag,
            });
        }
        return result;
    }

    /// <summary>Adds the bloomeries saved by <see cref="Capture"/> to an empty registry. One whose anchor no longer
    /// resolves, or whose numbers are out of range, is dropped, and so is a fuel that no longer resolves. Returns how
    /// many things were dropped.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.BloomeryData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key)
                || !SaveNames.TryResolve(data.Anchor, out var anchor)
                || data.Facing is < 0 or > 3
                || data.Ore < 0 || data.Ore > ProfileOf(anchor).OreCap
                || data.Fuel < 0 || data.Fuel > ProfileOf(anchor).FuelCap
                || data.Blooms < 0 || data.Slag < 0
                || data.Blooms > BloomeryConfig.MaxStoredOutput || data.Slag > BloomeryConfig.MaxStoredOutput
                || !float.IsFinite(data.FuelSeconds) || !float.IsFinite(data.BurnLeft) || !float.IsFinite(data.Heat)
                || !float.IsFinite(data.UnitProgress) || !float.IsFinite(data.UnitOverheat))
            {
                skipped++;
                continue;
            }
            var state = new BloomeryState
            {
                AnchorId = anchor,
                Profile = ProfileOf(anchor),
                Facing = data.Facing,
                Ore = data.Ore,
                Fuel = data.Fuel,
                FuelSeconds = data.FuelSeconds,
                Lit = data.Lit,
                BurnLeft = data.BurnLeft,
                Heat = Math.Clamp(data.Heat, 0f, 1f),
                UnitProgress = Math.Clamp(data.UnitProgress, 0f, BloomeryConfig.SecondsPerOre),
                UnitOverheat = Math.Max(0f, data.UnitOverheat),
                Blooms = data.Blooms,
                Slag = data.Slag,
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
            _bloomeries[key] = state;
        }
        RefreshCells();
        return skipped;
    }

    /// <summary>Writes every tracked bloomery, for the station broadcast (<see cref="StationNetCodec"/>).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_bloomeries.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), state) in _bloomeries)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.WriteId(state.AnchorId);
            writer.Write((byte)state.Facing);
            writer.Write((byte)state.Ore);
            writer.Write((byte)state.Fuel);
            writer.WriteId(state.FuelItemId);
            writer.Write(state.FuelSeconds);
            writer.Write(state.Lit);
            writer.Write(state.BurnLeft);
            writer.Write(state.Heat);
            writer.Write(state.UnitProgress);
            writer.Write(state.UnitOverheat);
            writer.Write((byte)state.Blooms);
            writer.Write((byte)state.Slag);
        }
    }

    /// <summary>Replaces the tracked bloomeries with a snapshot written by <see cref="WriteNet"/>: a client's
    /// display-only mirror, which never runs <see cref="Update"/>.</summary>
    internal void ReadNet(PacketReader reader)
    {
        _bloomeries.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var state = new BloomeryState
            {
                AnchorId = BlockTables.RequireAssigned(reader.ReadId()),
                Facing = reader.ReadByte(),
                Ore = reader.ReadByte(),
                Fuel = reader.ReadByte(),
                FuelItemId = BlockTables.RequireAssigned(reader.ReadId()),
                FuelSeconds = StationNetCodec.ReadFinite(reader),
                Lit = reader.ReadBool(),
                BurnLeft = StationNetCodec.ReadFinite(reader),
                Heat = StationNetCodec.ReadFinite(reader),
                UnitProgress = StationNetCodec.ReadFinite(reader),
                UnitOverheat = StationNetCodec.ReadFinite(reader),
                Blooms = reader.ReadByte(),
                Slag = reader.ReadByte(),
            };
            state.Profile = ProfileOf(state.AnchorId);
            if (state.Facing > 3 || state.Ore > state.Profile.OreCap || state.Fuel > state.Profile.FuelCap)
                throw new System.IO.InvalidDataException("bloomery snapshot out of range");
            state.Heat = Math.Clamp(state.Heat, 0f, 1f);
            _bloomeries[key] = state;
        }
        RefreshCells();
    }
}
