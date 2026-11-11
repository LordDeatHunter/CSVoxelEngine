using System.Collections.Generic;

namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/recipes/alloy/&lt;name&gt;.json</c>.</summary>
public sealed class AlloyRecipeDefinition
{
    /// <summary>Molten material one full set alloys into, by file name, e.g. <c>bronze</c>.</summary>
    public string? Result { get; set; }

    /// <summary>The molten materials one set takes and how much of each; the set yields their sum.</summary>
    public List<PartDefinition>? Parts { get; set; }

    /// <summary>One part of a set.</summary>
    public sealed class PartDefinition
    {
        /// <summary>Molten material, by file name, e.g. <c>copper</c>.</summary>
        public string? Fluid { get; set; }

        /// <summary>Milliliters of it one set takes.</summary>
        public int? Ml { get; set; }
    }
}
