namespace silkgltriangle;

/// <summary>One post-growth feature of a <see cref="TreeSpecies"/>: the stage and its depth in blocks.</summary>
public readonly record struct TreeFeatureSpec(ITreeFeature Feature, int Depth);
