namespace VoxelEngine.Effects.Climate;

/// <summary>
/// One mesh build's tint fields, both palettes, which come out of one climate evaluation per
/// corner (<see cref="BiomeTint.FieldsAt"/>).
/// </summary>
public readonly record struct BiomeTintFields(BiomeTintField Grass, BiomeTintField Foliage)
{
    /// <summary>The field of <paramref name="palette"/>.</summary>
    public BiomeTintField Field(BiomeTintPalette palette) =>
        palette == BiomeTintPalette.Foliage ? Foliage : Grass;
}
