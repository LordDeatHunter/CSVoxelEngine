namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The blocks one sky island is built of: its <see cref="Top"/> cell, <see cref="SoilDepth"/> cells of
/// <see cref="Soil"/> under it and a <see cref="Core"/> below. Every island wears <see cref="Default"/> (grass
/// over dirt over stone) for now: <see cref="SkyIslandGenerator"/> reads its palette in one place, which is
/// the seam the sky biomes of plans/LAYERS.md <c>L-13</c> plug into (a palette picked per island or per
/// column) without touching the shape or the stamp.
/// </summary>
public readonly record struct SkyIslandPalette(byte Top, byte Soil, byte Core, int SoilDepth)
{
    /// <summary>Grass over dirt over stone: the overworld's own blocks.</summary>
    public static readonly SkyIslandPalette Default = new(TerrainGenerator.SurfaceBlock, TerrainGenerator.SoilBlock,
        TerrainGenerator.RockBlock, SkyIslandConfig.IslandSoilDepth);

    /// <summary>The block at world y of a column whose top cell is <paramref name="top"/>.</summary>
    public byte BlockAt(int y, int top) => y == top ? Top : y >= top - SoilDepth ? Soil : Core;
}
