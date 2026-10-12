using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// How a block turns into chunk geometry. <see cref="Model"/> emits the baked quads of its
/// JSON model; the others are procedural emitters that predate models or need per-cell state
/// and still own their own geometry (ankle-high pebble clusters,
/// crossed-stick branch piles, layered snow boxes, the sloped translucent tops of a fluid).
/// <see cref="TreeWood"/> is the reworked trees' wood: one block per species whose thickness
/// and parent direction live in the cell's state byte (<see cref="WoodState"/>), drawn as a node
/// box plus one tube toward every connected neighbor (<see cref="TreeWoodShape"/>), or as a
/// plain culled cube at full thickness.
/// <see cref="Quern"/> replays only its model's base quads: the plate and handle spin as a
/// dynamic entity (see <see cref="QuernRenderer"/>), so the chunk never holds them.
/// <see cref="Multiblock"/> and <see cref="MultiblockPart"/> are the two halves of a structure
/// that spans a 2x2x2 volume of cells: the anchor (the volume's minimum corner) emits its whole
/// baked model — authored in 0..32 model units and pre-rotated to each facing, see
/// <see cref="MultiblockShape"/> — turned to the facing in the cell's state byte, while each of
/// the other seven cells is a part that draws nothing at all and only exists so the volume is
/// solid, occludes and can be mined.
/// <see cref="Pane"/> is a glass pane: a thin center post plus an arm toward every neighboring
/// pane or solid full cube, derived from the neighbors at mesh time (see <see cref="PaneShape"/>);
/// its model only supplies the tiles and the icon.
/// </summary>
public enum BlockRenderer
{
    Model,
    TreeWood,
    Pebbles,
    BranchPile,
    Fluid,
    Quern,
    SnowLayers,
    Multiblock,
    MultiblockPart,
    Pane,
}
