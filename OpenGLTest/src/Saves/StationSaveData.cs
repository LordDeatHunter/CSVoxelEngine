using System.Collections.Generic;

namespace VoxelEngine.Saves;

/// <summary>
/// The JSON shape of a world's <c>stations.json</c> (<see cref="StationSaveCodec"/>): the campfires,
/// querns, kilns and cast molds with their in-progress state. Every field is optional, so an older or
/// hand-edited file degrades field by field instead of failing the load; a cell travels as
/// <c>int[3]</c>. Content is referenced by <see cref="SaveNames"/> name (blocks and items) or molten
/// material file name, never by byte id; whoever restores the data drops an entry whose name no
/// longer resolves.
/// </summary>
public sealed class StationSaveData
{
    public List<CampfireData>? Campfires { get; set; }
    public List<QuernData>? Querns { get; set; }
    public List<KilnData>? Kilns { get; set; }
    public List<CastData>? Casts { get; set; }

    /// <summary>The bloomeries (iron smelters), keyed by anchor cell.</summary>
    public List<BloomeryData>? Bloomeries { get; set; }

    /// <summary>The anvils with a piece lying on them.</summary>
    public List<AnvilData>? Anvils { get; set; }

    /// <summary>The chests that have been used (a never-opened ruin chest is not here and re-rolls from its seed).</summary>
    public List<ChestData>? Chests { get; set; }

    /// <summary>The waystone registry's records (a waystone nobody activated has none).</summary>
    public List<WaystoneData>? Waystones { get; set; }

    /// <summary>The battle towers' floor locks (a tower nobody entered has none and starts sealed).</summary>
    public List<TowerData>? Towers { get; set; }

    /// <summary>One battle tower's lock state, keyed by its placement cell (see
    /// <see cref="Simulation.Tower.TowerManager"/>).</summary>
    public sealed class TowerData
    {
        /// <summary>The tower's placement cell as <c>int[2]</c> (cellX, cellZ).</summary>
        public int[]? Cell { get; set; }

        /// <summary>One entry per floor, ground floor first.</summary>
        public List<TowerFloorData>? Floors { get; set; }
    }

    /// <summary>One tower floor's lock state.</summary>
    public sealed class TowerFloorData
    {
        /// <summary>The waves each pile still raises, in the site's pile order.</summary>
        public int[]? Waves { get; set; }

        /// <summary>Whether the door sealing this floor from the next has opened (it stays open).</summary>
        public bool DoorOpen { get; set; }

        /// <summary>Bound skeletons that despawned or strayed and must be raised again.</summary>
        public int Owed { get; set; }
    }

    /// <summary>One waystone's registry row, keyed by its cell (see <see cref="Simulation.Waystones.WaystoneManager"/>).</summary>
    public sealed class WaystoneData
    {
        public int[]? Cell { get; set; }

        /// <summary>Its name (generated or the first discoverer's rename).</summary>
        public string? Name { get; set; }

        /// <summary>The discoverer id of the first player to activate it: a player name slug, or the local player's marker.</summary>
        public string? Discoverer { get; set; }

        /// <summary>Whether its block has been broken.</summary>
        public bool Lost { get; set; }
    }

    /// <summary>One chest block's contents, keyed by its cell (see <see cref="ChestSaveCodec"/>); also the shape of the
    /// network snapshot a player gets when it opens one.</summary>
    public sealed class ChestData
    {
        public int[]? Cell { get; set; }

        /// <summary>How many slots the chest has.</summary>
        public int? SlotCount { get; set; }

        /// <summary>One entry per non-empty slot.</summary>
        public List<ChestSlotData>? Slots { get; set; }
    }

    /// <summary>One non-empty chest slot: a stackable by name and count, or a tool or gear instance with its components.</summary>
    public sealed class ChestSlotData
    {
        public int? Slot { get; set; }
        public string? Name { get; set; }
        public int? Count { get; set; }

        /// <summary>The instance with its components, for a tool or piece of gear.</summary>
        public PlayerSaveData.ToolData? Instance { get; set; }
    }

    /// <summary>One branch pile with a fire and/or food on it.</summary>
    public sealed class CampfireData
    {
        public int[]? Cell { get; set; }
        public bool Lit { get; set; }
        public float BurnLeft { get; set; }
        public List<CookSlotData>? Slots { get; set; }
    }

    public sealed class CookSlotData
    {
        public string? Input { get; set; }
        public string? Output { get; set; }
        public int Count { get; set; }
        public float TimeLeft { get; set; }
        public float TotalTime { get; set; }
        public bool Done { get; set; }
    }

    public sealed class QuernData
    {
        public int[]? Cell { get; set; }

        /// <summary>The waiting crushable, null when the quern is empty.</summary>
        public string? Input { get; set; }
        public float Angle { get; set; }
        public bool Spinning { get; set; }
    }

    /// <summary>One kiln structure, keyed by its anchor cell.</summary>
    public sealed class KilnData
    {
        public int[]? Cell { get; set; }
        public string? Anchor { get; set; }
        public int Facing { get; set; }
        public int Fuel { get; set; }
        public string? FuelItem { get; set; }
        public float FuelSeconds { get; set; }
        public bool Lit { get; set; }
        public float BurnLeft { get; set; }

        /// <summary>The crucible standing in the hearth, null when there is none.</summary>
        public string? Crucible { get; set; }

        /// <summary>What that crucible holds (<see cref="CrucibleSaveCodec"/>), null for an empty vessel.</summary>
        public PlayerSaveData.CrucibleData? Contents { get; set; }
        public float MeltLeft { get; set; }
    }

    /// <summary>One bloomery structure, keyed by its anchor cell.</summary>
    public sealed class BloomeryData
    {
        public int[]? Cell { get; set; }
        public string? Anchor { get; set; }
        public int Facing { get; set; }
        public int Ore { get; set; }
        public int Fuel { get; set; }
        public string? FuelItem { get; set; }
        public float FuelSeconds { get; set; }
        public bool Lit { get; set; }
        public float BurnLeft { get; set; }
        public float Heat { get; set; }
        public float UnitProgress { get; set; }
        public float UnitOverheat { get; set; }

        /// <summary>Finished blooms waiting to be collected.</summary>
        public int Blooms { get; set; }

        /// <summary>Slag waiting to be collected.</summary>
        public int Slag { get; set; }
    }

    /// <summary>One anvil with a piece on it.</summary>
    public sealed class AnvilData
    {
        public int[]? Cell { get; set; }
        public string? Block { get; set; }
        public int Hits { get; set; }
        public float Cooldown { get; set; }
    }

    /// <summary>One mold cell holding a pour, its recipe named by mold block and material.</summary>
    public sealed class CastData
    {
        public int[]? Cell { get; set; }
        public string? Mold { get; set; }
        public string? Material { get; set; }
        public int FilledMl { get; set; }
        public bool Cooling { get; set; }
        public float CoolLeft { get; set; }
        public bool Done { get; set; }
    }
}
