namespace silkgltriangle;

/// <summary>JSON shape of <c>assets/data/recipes/crush/&lt;name&gt;.json</c>.</summary>
public sealed class CrushRecipeDefinition
{
    /// <summary>Crushable input id, e.g. <c>item/malachite_ore</c>.</summary>
    public string? Input { get; set; }

    /// <summary>Crushed result id, e.g. <c>item/crushed_copper</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one crush makes; default 1.</summary>
    public int? Count { get; set; }
}
