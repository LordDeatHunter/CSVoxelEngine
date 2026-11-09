namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// The blocks one <see cref="CaveBiome"/> dresses its rock in. Every block is solid, opaque and occluding,
/// never a fluid, a falling block or one that needs support, so swapping stone for it never changes what a
/// fluid, a support check or the cave's openness sees.
/// </summary>
/// <param name="Stone">The biome's base rock: every stone cell inside the biome's volume that is not a
/// floor or a ceiling.</param>
/// <param name="Floor">A rock cell with open cave above it.</param>
/// <param name="Ceiling">A rock cell with open cave below it.</param>
/// <param name="Accent">The block one cave face cell (any role but interior) in
/// <paramref name="AccentRate"/> wears instead.</param>
/// <param name="AccentRate">One face cell in this many is <paramref name="Accent"/>; 0 for none.</param>
public readonly record struct CaveBiomePalette(byte Stone, byte Floor, byte Ceiling, byte Accent, int AccentRate);
