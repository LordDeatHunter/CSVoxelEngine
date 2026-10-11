using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The JSON shape of a world's <c>player.json</c>: where the player stands and looks, which
/// modes are on, their health, the lighting clock and everything they carry.
///
/// <para>
/// Content is referenced by name only, through <see cref="SaveNames"/> (<c>block/&lt;name&gt;</c>,
/// <c>item/&lt;name&gt;</c>, molten materials by file name), never by a byte id, since ids are
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

    /// <summary>The absolute lighting clock (window time plus the world's time offset), so
    /// the time of day resumes where it was.</summary>
    public double? LightingClock { get; set; }

    /// <summary>The eye position the player respawns at, <c>[x, y, z]</c>.</summary>
    public float[]? RespawnEye { get; set; }

    public InventoryData? Inventory { get; set; }

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

        /// <summary>A player rename, or null for the definition name.</summary>
        public string? CustomName { get; set; }

        public Dictionary<string, int>? Enchantments { get; set; }
    }

    /// <summary>What a crucible holds: <see cref="CrucibleComponent"/> by name.</summary>
    public sealed class CrucibleData
    {
        /// <summary>Molten material file name, or null for "no material".</summary>
        public string? Material { get; set; }

        public int? MoltenMl { get; set; }

        public int? PendingMl { get; set; }

        /// <summary>The last melt input charged in, as a save name, or null.</summary>
        public string? PendingInput { get; set; }
    }

    /// <summary>One hotbar slot: a tool instance reference or a stackable name (null = empty).</summary>
    public sealed class HotbarSlotData
    {
        public bool? IsTool { get; set; }

        public string? Name { get; set; }

        public int? ToolInstance { get; set; }
    }
}
