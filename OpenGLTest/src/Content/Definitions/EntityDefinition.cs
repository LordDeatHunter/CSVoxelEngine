namespace VoxelEngine.Content.Definitions;

/// <summary>JSON shape of <c>assets/data/entities/&lt;name&gt;.json</c>.</summary>
public sealed class EntityDefinition
{
    /// <summary>Display name; defaults to the file name when omitted.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Maximum (and starting) hit points of one spawn. Optional, at least 1 when present. An
    /// entity with no hp has no health pool and cannot back a mob.
    /// </summary>
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

    /// <summary>
    /// Blockbench projects to render the entity from, relative to the working directory, one per
    /// size class or look; each supplies its own skin like <see cref="Model"/>. When present the
    /// list is non-empty and <see cref="Model"/> must be omitted; the first entry is the model.
    /// </summary>
    public string[]? Variants { get; set; }
}
