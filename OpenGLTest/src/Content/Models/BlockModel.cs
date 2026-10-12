using System.Collections.Generic;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.Content.Models;

/// <summary>
/// A model with its parent chain already flattened: every texture variable merged
/// child-over-parent and the element list taken from the nearest ancestor that declares one.
/// Texture <c>#refs</c> are still unresolved here — they are turned into atlas tiles by
/// <see cref="ModelBaker"/>, which runs after the atlas exists.
/// </summary>
/// <param name="Source">File path (or owning block) the model came from, for error messages.</param>
/// <param name="Textures">Merged texture variables, keyed without the leading '#'.</param>
/// <param name="Elements">Boxes to bake, in 0..16 model space.</param>
public sealed record BlockModel(
    string Source,
    IReadOnlyDictionary<string, string> Textures,
    IReadOnlyList<ModelDefinition.ElementDefinition> Elements);
