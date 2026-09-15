namespace silkgltriangle;

/// <summary>
/// How a block turns into chunk geometry. <see cref="Model"/> emits the baked quads of its
/// JSON model; the others are procedural emitters that predate models or need per-cell state
/// and still own their own geometry (tapering wood tubes, ankle-high pebble clusters,
/// crossed-stick branch piles, layered snow boxes, the sloped translucent tops of a fluid).
/// <see cref="Quern"/> replays only its model's base quads: the plate and handle spin as a
/// dynamic entity (see <see cref="QuernRenderer"/>), so the chunk never holds them.
/// </summary>
public enum BlockRenderer
{
    Model,
    TaperedWood,
    Pebbles,
    BranchPile,
    Fluid,
    Quern,
    SnowLayers,
}
