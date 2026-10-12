using VoxelEngine.Entities;

namespace VoxelEngine.Content.Blocks;

/// <summary>What happens when a block is destroyed: roll its loot table like normal.
/// <see cref="PrimeTnt"/> additionally marks the block as primable: a firestarter strike or
/// a nearby blast spawns a <see cref="PrimedTnt"/> instead of dropping.</summary>
public enum BlockBreakAction
{
    Drop,
    PrimeTnt,
}
