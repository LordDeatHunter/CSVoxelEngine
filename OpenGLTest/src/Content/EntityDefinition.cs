namespace silkgltriangle;

/// <summary>JSON shape of <c>assets/data/entities/&lt;name&gt;.json</c>.</summary>
public sealed class EntityDefinition
{
    /// <summary>Display name; defaults to the file name when omitted.</summary>
    public string? Name { get; set; }

    /// <summary>Maximum (and starting) hit points of one spawn. Required, at least 1.</summary>
    public int? Hp { get; set; }

    /// <summary>
    /// Loot table rolled when the entity dies, by file name. Omitted or <c>"none"</c> drops
    /// nothing. A named table must exist (same schema as block loot).
    /// </summary>
    public string? Loot { get; set; }

    /// <summary>
    /// Blockbench project to render the entity from, relative to the working directory.
    /// Omitted means <c>assets/models/entity/&lt;name&gt;.bbmodel</c>. The project's own
    /// <c>textures</c> entry supplies the skin, resolved next to the model file.
    /// </summary>
    public string? Model { get; set; }
}
