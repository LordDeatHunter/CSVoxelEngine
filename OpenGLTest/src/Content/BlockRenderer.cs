namespace silkgltriangle;

/// <summary>
/// How a block turns into chunk geometry. <see cref="Model"/> emits the baked quads of its
/// JSON model; the other two are procedural emitters that predate models and still own their
/// own geometry (tapering wood tubes, ankle-high pebble clusters).
/// </summary>
public enum BlockRenderer
{
    Model,
    TaperedWood,
    Pebbles,
}
