using System.Text.Json;

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
    /// What a hit sprays off the entity. Omitted is red blood; <c>"none"</c> sprays nothing (bone, stone);
    /// <c>{"color": [r, g, b]}</c> (sRGB 0..255) sprays that color (a spider's blue ichor).
    /// </summary>
    public JsonElement? Blood { get; set; }

    /// <summary>
    /// Status effect file names (<c>assets/data/effects</c>) this entity can never be given: an effect applied to it is
    /// refused (the hell and magma mobs are immune to <c>burning</c>). Omitted is immune to nothing; every name is
    /// checked against the loaded effects.
    /// </summary>
    public string[]? Immune { get; set; }

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

    /// <summary>
    /// An item every spawn holds in one hand (the skeleton warrior's copper dagger), drawn from its own
    /// sprite at that hand by the entity, shadow and item-debugger passes. Omitted holds nothing.
    /// </summary>
    public HeldDefinition? Held { get; set; }

    /// <summary>The <c>held</c> object: the item, the rig group whose hand holds it and how it sits there.</summary>
    public sealed class HeldDefinition
    {
        /// <summary>A pure item's file name (required).</summary>
        public string? Item { get; set; }

        /// <summary>The model group holding it (required, in every variant): the item rides the bottom of
        /// that group's lowest cube (<c>EntityAnimator.HandAnchor</c>).</summary>
        public string? Part { get; set; }

        /// <summary>The item's transform in that hand's space, the same shape and composition as an item
        /// file's <c>display</c> (rotation [pitch, yaw, roll] degrees, translation in blocks, scale of the
        /// one-block sprite); omitted takes the same defaults.</summary>
        public ItemDefinition.DisplayDefinition? Display { get; set; }
    }
}
