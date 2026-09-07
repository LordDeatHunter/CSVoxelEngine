namespace silkgltriangle;

/// <summary>
/// How a block turns into chunk geometry. <see cref="Model"/> emits the baked quads of its
/// JSON model; the others are procedural emitters that predate models or need per-cell state
/// and still own their own geometry (tapering wood tubes, ankle-high pebble clusters, the
/// sloped translucent tops of a fluid).
/// </summary>
public enum BlockRenderer
{
    Model,
    TaperedWood,
    Pebbles,
    Fluid,
}
