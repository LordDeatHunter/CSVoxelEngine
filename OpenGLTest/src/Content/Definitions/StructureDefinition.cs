using System.Collections.Generic;
using System.Text.Json;

namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/structures/&lt;name&gt;.json</c>: a hand-authored template the ruin placements stamp.
/// Layers run bottom to top, each an array of rows (north to south), each row a string of palette keys (west to east):
/// a space writes nothing, <c>.</c> writes air.</summary>
public sealed class StructureDefinition
{
    /// <summary><c>surface</c>, <c>underground</c>, <c>hell</c> or <c>sky</c>: which placement stamps it.</summary>
    public string? Kind { get; set; }

    /// <summary>Relative pick weight among the templates of its kind (default 1).</summary>
    public float? Weight { get; set; }

    /// <summary>Surface only: the biome names it may stand in; omitted means every land biome.</summary>
    public string[]? Biomes { get; set; }

    /// <summary>Index of the layer whose cells sit on the anchor's floor level (default 0): the layers under it are dug in.</summary>
    public int? GroundLayer { get; set; }

    /// <summary>Palette key to a block name, or to an object <c>{"block": name, "facing": north|south|west|east}</c>.</summary>
    public Dictionary<string, JsonElement>? Palette { get; set; }

    /// <summary>The layers, bottom first.</summary>
    public string[][]? Layers { get; set; }
}
