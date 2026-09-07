using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// JSON shape of a Minecraft Java block model — what BlockBench exports. Only the fields the
/// engine bakes are declared; <c>display</c>, <c>gui_light</c>, <c>ambientocclusion</c> and
/// <c>tintindex</c> are accepted and ignored.
/// </summary>
public sealed class ModelDefinition
{
    /// <summary>Model this one inherits from, e.g. <c>"block/cube_all"</c>.</summary>
    public string? Parent { get; set; }

    /// <summary>Texture variables; values are texture names or <c>#refs</c> into the parent.</summary>
    public Dictionary<string, string>? Textures { get; set; }

    /// <summary>Boxes making up the model. A child that declares any element replaces the
    /// parent's list wholesale, exactly like Minecraft.</summary>
    public List<ElementDefinition>? Elements { get; set; }

    /// <summary>One box of a model, in 0..16 model space.</summary>
    public sealed class ElementDefinition
    {
        public float[]? From { get; set; }
        public float[]? To { get; set; }
        public RotationDefinition? Rotation { get; set; }

        /// <summary>Whether the box takes directional shading; default true.</summary>
        public bool? Shade { get; set; }

        public Dictionary<string, FaceDefinition>? Faces { get; set; }
    }

    /// <summary>One textured side of an element.</summary>
    public sealed class FaceDefinition
    {
        /// <summary><c>[u1, v1, u2, v2]</c> in 0..16; derived from the box when omitted.</summary>
        public float[]? Uv { get; set; }

        /// <summary>Texture variable reference, e.g. <c>"#side"</c>, or a bare texture name.</summary>
        public string? Texture { get; set; }

        /// <summary>Side of the cell this face is culled against, or null for never.</summary>
        public string? Cullface { get; set; }

        /// <summary>Texture rotation in degrees: 0, 90, 180 or 270.</summary>
        public int? Rotation { get; set; }
    }

    /// <summary>Rotation of an element about one axis, in Minecraft's restricted form.</summary>
    public sealed class RotationDefinition
    {
        public float[]? Origin { get; set; }
        public string? Axis { get; set; }
        public float Angle { get; set; }

        /// <summary>Scale the box back out so rotated faces still meet their neighbours.</summary>
        public bool? Rescale { get; set; }
    }
}
