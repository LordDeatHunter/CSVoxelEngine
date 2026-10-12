namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/recipes/cast/&lt;result&gt;.json</c>.</summary>
public sealed class CastRecipeDefinition
{
    /// <summary>Fired mold block the metal is poured into, e.g. <c>block/fired_mold_ingot</c>.</summary>
    public string? Mold { get; set; }

    /// <summary>Molten material poured in, by file name, e.g. <c>copper</c>.</summary>
    public string? Fluid { get; set; }

    /// <summary>Milliliters the mold takes before it is full.</summary>
    public int? AmountMl { get; set; }

    /// <summary>Seconds a full mold takes to cool into its result.</summary>
    public float? CoolSeconds { get; set; }

    /// <summary>Block or item the cooled cast yields, e.g. <c>item/copper_ingot</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one cast makes; default 1.</summary>
    public int? Count { get; set; }
}
