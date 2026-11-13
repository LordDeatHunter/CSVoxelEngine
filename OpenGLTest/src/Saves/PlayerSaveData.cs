using System.Collections.Generic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Saves;

/// <summary>
/// The JSON shape of a world's <c>player.json</c>: where the player stands and looks, which
/// modes are on, their health and mana, the lighting clock and everything they carry.
///
/// <para>
/// Content is referenced by name only, through <see cref="SaveNames"/> (<c>block/&lt;name&gt;</c>,
/// <c>item/&lt;name&gt;</c>, molten materials by file name), never by a numeric id, since ids are
/// renumbered whenever the content set changes. Vectors travel as <c>float[3]</c> (x, y, z).
/// Every field is optional, so an older or hand-edited file degrades field by field: a missing
/// value keeps whatever a fresh world would have, and a name this run does not know is dropped
/// with a warning.
/// </para>
/// </summary>
public sealed class PlayerSaveData
{
    /// <summary>Camera eye position, <c>[x, y, z]</c>.</summary>
    public float[]? Position { get; set; }

    /// <summary>View yaw in degrees.</summary>
    public float? Yaw { get; set; }

    /// <summary>View pitch in degrees.</summary>
    public float? Pitch { get; set; }

    public bool? Creative { get; set; }

    public bool? Flying { get; set; }

    public int? Health { get; set; }

    /// <summary>Mana left in the pool; absent reads as a full pool. The max is not saved: it follows
    /// the player's progression.</summary>
    public float? Mana { get; set; }

    /// <summary>The absolute lighting clock (window time plus the world's time offset), so
    /// the time of day resumes where it was.</summary>
    public double? LightingClock { get; set; }

    /// <summary>The eye position the player respawns at, <c>[x, y, z]</c>.</summary>
    public float[]? RespawnEye { get; set; }

    public InventoryData? Inventory { get; set; }

    /// <summary>The player's quest progress; null when the save predates quests (nothing done yet).</summary>
    public QuestSaveData? Quests { get; set; }

    /// <summary>Every pool, every tool instance and the hotbar layout.</summary>
    public sealed class InventoryData
    {
        public List<StackData>? Blocks { get; set; }

        public List<StackData>? Misc { get; set; }

        public List<ToolData>? Tools { get; set; }

        /// <summary>The hotbar slots in slot order.</summary>
        public List<HotbarSlotData>? Hotbar { get; set; }

        public int? SelectedIndex { get; set; }

        /// <summary>The next tool instance id to hand out, so restored and new instances
        /// never collide.</summary>
        public int? NextToolInstance { get; set; }

        /// <summary>Names of the ids this player has obtained (the look-at label's "Unknown"
        /// rule). Absent, as in a save written before it existed, reads as empty.</summary>
        public List<string>? Known { get; set; }

        /// <summary>The spell part keys (<c>&lt;kind&gt;/&lt;name&gt;</c>) this player has learned. Null in
        /// an inventory saved before known parts existed (MG-8), which is how such a player is told apart
        /// from one who simply knows none.</summary>
        public List<string>? KnownParts { get; set; }

        /// <summary>The worn gear: one instance id per equipment slot (armor slots first, then accessories),
        /// 0 for an empty slot. The gear instances themselves are in <see cref="Tools"/>. Null in a save from
        /// before equipment existed.</summary>
        public List<int>? Equipped { get; set; }

        /// <summary>The waystones this player has discovered, each with the name it had then. Null in an inventory
        /// saved before waystones existed; written as a list, empty or not.</summary>
        public List<DiscoveredWaystoneData>? Waystones { get; set; }

        /// <summary>The battle towers this player has marked with a tower map, as [x, z] center columns, oldest first.
        /// Null in an inventory saved before tower maps existed.</summary>
        public List<int[]>? TowerMarks { get; set; }
    }

    /// <summary>One discovered waystone: its cell and the name the player knows it by.</summary>
    public sealed class DiscoveredWaystoneData
    {
        public int[]? Cell { get; set; }

        public string? Name { get; set; }
    }

    /// <summary>One stackable pool entry: a block or item name and its pool total.</summary>
    public sealed class StackData
    {
        public string? Name { get; set; }

        public int? Count { get; set; }
    }

    /// <summary>One tool instance and the per-stack components it carries.</summary>
    public sealed class ToolData
    {
        public int? InstanceId { get; set; }

        public string? Name { get; set; }

        /// <summary>Durability left; only written for a tool that wears out.</summary>
        public int? UsesLeft { get; set; }

        /// <summary>The maximum it was saved against; informational, this run's maximum wins.</summary>
        public int? MaxUses { get; set; }

        /// <summary>A crucible's contents; only written for a crucible.</summary>
        public CrucibleData? Crucible { get; set; }

        public Simulation.Lanterns.LanternData? Lantern { get; set; }

        /// <summary>A staff's bound spells; only written for a staff with something bound or selected.</summary>
        public StaffData? Staff { get; set; }

        /// <summary>A player rename, or null for the definition name.</summary>
        public string? CustomName { get; set; }

        public Dictionary<string, int>? Enchantments { get; set; }

        /// <summary>A gear instance's rarity and rolled modifiers; only written for gear that rolled any.</summary>
        public RolledData? Rolled { get; set; }
    }

    /// <summary>What a piece of loot gear rolled: <see cref="ModifiersComponent"/> by name, never by id.</summary>
    public sealed class RolledData
    {
        /// <summary>The rarity name (common to legendary).</summary>
        public string? Rarity { get; set; }

        /// <summary>The rolled modifiers.</summary>
        public List<RolledModifierData>? Modifiers { get; set; }
    }

    /// <summary>One rolled modifier: a stat name and its value.</summary>
    public sealed class RolledModifierData
    {
        public string? Stat { get; set; }

        public float Value { get; set; }
    }

    /// <summary>What a crucible holds: <see cref="CrucibleComponent"/> by name, read and written by
    /// <see cref="CrucibleSaveCodec"/> (in <c>player.json</c>, <c>stations.json</c> and the inventory
    /// snapshot alike).</summary>
    public sealed class CrucibleData
    {
        /// <summary>One entry per metal held; null or empty for an empty vessel.</summary>
        public List<MetalData>? Metals { get; set; }

        /// <summary>The last melt input charged in, as a save name, or null.</summary>
        public string? PendingInput { get; set; }
    }

    /// <summary>One metal in a crucible (<see cref="CrucibleMetal"/>) by name.</summary>
    public sealed class MetalData
    {
        /// <summary>Molten material file name.</summary>
        public string? Material { get; set; }

        public int MoltenMl { get; set; }

        public int PendingMl { get; set; }
    }

    /// <summary>What a staff has bound: <see cref="StaffComponent"/> by spell part key, never by id.</summary>
    public sealed class StaffData
    {
        /// <summary>One entry per slot, each the part keys (<c>&lt;kind&gt;/&lt;name&gt;</c>) of its bound recipe, null for an empty slot.</summary>
        public List<List<string>?>? Slots { get; set; }

        /// <summary>The selected slot.</summary>
        public int? Selected { get; set; }
    }

    /// <summary>One hotbar slot: a tool instance reference or a stackable name (null = empty).</summary>
    public sealed class HotbarSlotData
    {
        public bool? IsTool { get; set; }

        public string? Name { get; set; }

        public int? ToolInstance { get; set; }
    }
}
