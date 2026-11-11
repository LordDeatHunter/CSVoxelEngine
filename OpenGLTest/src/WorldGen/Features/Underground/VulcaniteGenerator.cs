using System;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Hell;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Vulcanite ore veins, the tier above iron: the same <see cref="OreNoiseLattice"/> deposits on a noise of their own,
/// stamped over basalt and nothing else, so they sit only in the barrier band (<see cref="BarrierGenerator"/>) and in
/// the Magma Caverns' floors, walls and ceilings (<see cref="HellBiomes"/>), never above
/// <see cref="WorldGenerationConfig.BarrierTopY"/>. The vein threshold drops near lava by
/// <see cref="WorldGenerationConfig.VulcaniteNearLavaShift"/> scaled by <see cref="LavaProximity"/>, so the ore gathers
/// by the pools and is all but absent away from them. Stamped right after hell, over the band and hell alike, before
/// any hell tree or ruin; nothing stamped later writes into the band. A pure function of world position and seed:
/// <see cref="HasOre"/> is what <see cref="Stamp"/> writes on every basalt cell, bit for bit across chunk seams.
/// </summary>
public static class VulcaniteGenerator
{
    private static FastNoiseLite VeinNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort OreBlock;

    /// <summary>The one block the ore replaces: basalt, the barrier's and the Magma Caverns' rock.</summary>
    public static readonly ushort HostBlock;

    static VulcaniteGenerator()
    {
        Reseed();
        OreBlock = ContentRegistry.RequireBlock("vulcanite_ore").Id;
        HostBlock = ContentRegistry.RequireBlock("basalt").Id;
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() => VeinNoise = TerrainGenerator.MakeNoise(186, WorldGenerationConfig.VulcaniteFrequency);

    /// <summary>
    /// How close cell (x, y, z) lies to lava, 0..1, from the generators' own pure fields and never a neighbor chunk.
    /// In hell it falls linearly from 1 at its layer's lava level (<see cref="HellGenerator.RowsAboveLava"/>) to 0
    /// <see cref="WorldGenerationConfig.VulcaniteLavaReachBlocks"/> rows above it: basalt lines only the Magma Caverns,
    /// whose every pool is lava, so that level is the lava's surface under every basalt cell. In the barrier band it
    /// is 1 in the deep lava pockets' patches (<see cref="LavaGenerator.PatchValue"/>) and fades to 0
    /// <see cref="WorldGenerationConfig.VulcanitePatchMargin"/> under their edge. 0 above the band.
    /// </summary>
    public static float LavaProximity(int x, int y, int z)
    {
        if (y > WorldGenerationConfig.BarrierTopY) return 0f;
        if (!HellGenerator.InHell(y))
            return Math.Clamp(
                (LavaGenerator.PatchValue(x, z) - WorldGenerationConfig.LavaPatchThreshold) / WorldGenerationConfig.VulcanitePatchMargin + 1f,
                0f, 1f);

        var rows = HellGenerator.RowsAboveLava(y);
        if (rows < 0) return 0f;
        return Math.Max(0f, 1f - (float)rows / WorldGenerationConfig.VulcaniteLavaReachBlocks);
    }

    /// <summary>Whether the cell, if basalt, is vulcanite: at or under the band's top and over the lattice threshold
    /// lowered by its lava proximity. Equals what <see cref="Stamp"/> writes on a basalt cell.</summary>
    public static bool HasOre(int x, int y, int z) =>
        y <= WorldGenerationConfig.BarrierTopY
        && OreNoiseLattice.Exceeds(VeinNoise, x, y, z, WorldGenerationConfig.VulcaniteThreshold + ShiftAt(x, y, z));

    /// <summary>What the stamp leaves at cell (x, y, z) that held <paramref name="block"/>: the ore over basalt where
    /// <see cref="HasOre"/> says so, else the block itself.</summary>
    public static ushort Over(ushort block, int x, int y, int z) =>
        block == HostBlock && HasOre(x, y, z) ? OreBlock : block;

    /// <summary>Replaces the basalt cells of the chunk whose minimum corner is (worldX0, worldY0, worldZ0) that lie in
    /// a vein; nothing else is touched.</summary>
    public static void Stamp(ushort[] blocks, int worldX0, int worldY0, int worldZ0) =>
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.VulcaniteThreshold, worldX0, worldY0, worldZ0,
            new VeinRule(WorldGenerationConfig.BarrierTopY - worldY0, worldX0, worldY0, worldZ0));

    /// <summary>Every world y <see cref="Stamp"/> can write: the barrier band and all of hell under it.</summary>
    internal static StampBand WriteBand() => new(int.MinValue, WorldGenerationConfig.BarrierTopY);

    /// <summary>What a cell's lava proximity adds to the threshold: <see cref="WorldGenerationConfig.VulcaniteNearLavaShift"/>
    /// at the lava, 0 out of its reach.</summary>
    private static float ShiftAt(int x, int y, int z) => WorldGenerationConfig.VulcaniteNearLavaShift * LavaProximity(x, y, z);

    /// <summary>Vulcanite replaces basalt only, at or under the band's top (as chunk-local Y), its threshold shifted
    /// by each cell's lava proximity.</summary>
    private readonly struct VeinRule(int maxLocalY, int worldX0, int worldY0, int worldZ0) : IOreLatticeRule
    {
        public bool IsHost(ushort block, int localX, int localY, int localZ) => localY <= maxLocalY && block == HostBlock;

        public ushort Replacement(int worldX, int worldY, int worldZ) => OreBlock;

        public float ThresholdShift(int localX, int localY, int localZ) =>
            ShiftAt(worldX0 + localX, worldY0 + localY, worldZ0 + localZ);

        // A proximity of at most 1 scales the shift no further down than the shift itself
        public float MinThresholdShift => WorldGenerationConfig.VulcaniteNearLavaShift;
    }
}
