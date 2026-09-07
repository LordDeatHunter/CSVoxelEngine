namespace silkgltriangle;

/// <summary>JSON shape of <c>assets/data/items/&lt;name&gt;.json</c>.</summary>
public sealed class ItemDefinition
{
    /// <summary>Display name; defaults to the file name when omitted.</summary>
    public string? Name { get; set; }

    /// <summary>Sprite path relative to the working directory, like every other asset.</summary>
    public string? Texture { get; set; }
}
