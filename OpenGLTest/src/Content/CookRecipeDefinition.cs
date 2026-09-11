namespace silkgltriangle;

/// <summary>JSON shape of <c>assets/data/recipes/cook/&lt;name&gt;.json</c>.</summary>
public sealed class CookRecipeDefinition
{
    /// <summary>Raw input id, e.g. <c>item/mudball</c>.</summary>
    public string? Input { get; set; }

    /// <summary>Cooked result id, e.g. <c>item/mud_brick</c>.</summary>
    public string? Result { get; set; }

    /// <summary>How many of the result one cook makes; default 1.</summary>
    public int? Count { get; set; }

    /// <summary>Seconds on a lit campfire before the input converts.</summary>
    public float? CookSeconds { get; set; }
}
