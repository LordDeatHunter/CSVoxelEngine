using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Ocean;

/// <summary>One-time name resolution for the generated habitat stamp.</summary>
internal static class OceanHabitatBlocks
{
    internal static readonly ushort Coral = ContentRegistry.RequireBlock("coral_amber").Id;
    internal static readonly ushort Rose = ContentRegistry.RequireBlock("coral_rose").Id;
    internal static readonly ushort Blue = ContentRegistry.RequireBlock("coral_blue").Id;
    internal static readonly ushort SeaMoss = ContentRegistry.RequireBlock("sea_moss").Id;
    internal static readonly ushort GlowMoss = ContentRegistry.RequireBlock("glow_moss").Id;
}
