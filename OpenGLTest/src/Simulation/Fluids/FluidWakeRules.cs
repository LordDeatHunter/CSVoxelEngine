using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>The pure rules of where a fluid may flow, and where loaded water wakes into: static and stateless, so the chunk-generation workers' frontier scan (<see cref="VoxelEngine.WorldGen.ChunkTerrainFill"/>) and the main-thread simulation share them.</summary>
internal static class FluidWakeRules
{
    /// <summary>
    /// Whether a block a fluid already knows is loaded is somewhere it may flow: not fluid
    /// itself, and either air or a fragile block the flow washes away. Shared with
    /// <see cref="Chunk.FluidFrontier"/>'s worker-thread scan so both halves of the wake-up
    /// agree with <see cref="FluidSpread.CanFlowInto"/> on what counts as somewhere to go.
    /// </summary>
    internal static bool IsFlowTarget(ushort id) => !BlockType.IsFluid(id) && BlockType.IsReplaceable(id);

    /// <summary>
    /// Whether a generated fluid may wake into a cell: a flow target at or below its column's
    /// natural terrain top, i.e. a carver opened it (a cave, an entrance, a breach). Open air
    /// above the terrain — a ravine, a beach, a hillside — is not one, so a generated sea stays
    /// static across them exactly like Minecraft's and only floods the caves that breach it.
    /// This is the first clause of <see cref="IsWakeTarget"/>, which both halves of the wake (the
    /// worker-thread <see cref="Chunk.FluidFrontier"/> scan and the border scan) now call.
    /// </summary>
    internal static bool IsCarvedFlowTarget(ushort id, int worldY, int columnHeight) =>
        IsFlowTarget(id) && worldY <= columnHeight;

    /// <summary>
    /// Whether a fluid cell should wake, when its chunk loads, into the neighbor
    /// <paramref name="targetId"/> at <paramref name="targetY"/>. The first clause is
    /// <see cref="IsCarvedFlowTarget"/>'s carved-cavity rule (a flow target at or below its own
    /// column's terrain top). The second admits any flow target, but only beside a fluid that
    /// sits above BOTH its own column's terrain top and its water level — which generated water
    /// (always at or below <see cref="ColumnData.WaterLevel"/>) and generated lava (always buried,
    /// at or below <see cref="ColumnData.Heights"/>) never are. So a generated chunk wakes exactly
    /// as it did under the carved rule, and only water the player poured (brought back by a
    /// restored chunk) wakes against any replaceable neighbor. Shared by
    /// <see cref="Chunk.FluidFrontier"/>'s worker-thread scan and <see cref="FluidWakeScanner.WakeBorders"/>.
    /// </summary>
    internal static bool IsWakeTarget(ushort targetId, int targetY, int targetColumnHeight,
        int fluidY, int fluidColumnHeight, int fluidColumnWaterLevel) =>
        IsFlowTarget(targetId)
        && (targetY <= targetColumnHeight
            || (fluidY > fluidColumnHeight && fluidY > fluidColumnWaterLevel));
}
