using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen;

/// <summary>
/// 3D-noise caves: "cheese" rooms where a low-frequency field is high, "spaghetti" tunnels along
/// the curves where two fields are both near zero (a wide and a narrow variant), and rare flat
/// caverns deep down. Every field is sampled on a coarse lattice per chunk and trilinearly
/// interpolated per block before thresholding, so the cost is a few hundred noise evaluations
/// per chunk instead of tens of thousands. All carving is a pure function of world position and
/// the column data, so neighboring chunks agree at their borders without any shared state.
/// </summary>
public static class NoiseCaves
{
    private const int Stride = WorldGenerationConfig.CaveNoiseStride;
    private const int Grid = Chunk.Size / Stride + 1;      // lattice points per axis, both chunk faces included
    private const int GridSq = Grid * Grid;
    private const int GridCount = Grid * GridSq;
    private const float InvStride = 1f / Stride;

    // Channel slots in the sample buffer
    private const int ChCheese = 0;
    private const int ChSpaghettiA = 1;
    private const int ChSpaghettiB = 2;
    private const int ChThickness = 3;
    private const int ChNarrowA = 4;
    private const int ChNarrowB = 5;
    private const int ChCavern = 6;
    private const int ChNarrowMask = 7;
    private const int Channels = 8;

    private static FastNoiseLite Cheese;
    private static FastNoiseLite SpaghettiA;
    private static FastNoiseLite SpaghettiB;
    private static FastNoiseLite Thickness;
    private static FastNoiseLite NarrowA;
    private static FastNoiseLite NarrowB;
    private static FastNoiseLite NarrowMask;
    private static FastNoiseLite Cavern;

    static NoiseCaves() => Reseed();

    /// <summary>
    /// Builds every cave noise field from the current <see cref="WorldGenerationConfig.Seed"/>: once
    /// from the static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker runs.
    /// </summary>
    internal static void Reseed()
    {
        Cheese = TerrainGenerator.MakeNoise(101, WorldGenerationConfig.CheeseFrequency, octaves: 2);
        SpaghettiA = TerrainGenerator.MakeNoise(111, WorldGenerationConfig.SpaghettiFrequency);
        SpaghettiB = TerrainGenerator.MakeNoise(112, WorldGenerationConfig.SpaghettiFrequency);
        Thickness = TerrainGenerator.MakeNoise(113, WorldGenerationConfig.SpaghettiThicknessFrequency);
        NarrowA = TerrainGenerator.MakeNoise(121, WorldGenerationConfig.NarrowSpaghettiFrequency);
        NarrowB = TerrainGenerator.MakeNoise(122, WorldGenerationConfig.NarrowSpaghettiFrequency);
        NarrowMask = TerrainGenerator.MakeNoise(123, WorldGenerationConfig.NarrowSpaghettiMaskFrequency);
        Cavern = TerrainGenerator.MakeNoise(131, WorldGenerationConfig.CavernFrequency);
    }

    /// <summary>
    /// Clears cave air out of a freshly filled chunk. <paramref name="blocks"/> is the chunk's
    /// block array (Chunk.Idx layout) and (worldX0, worldY0, worldZ0) its lowest corner.
    /// </summary>
    public static void Carve(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        var samples = ArrayPool<float>.Shared.Rent(Channels * GridCount);
        try
        {
            SampleLattice(samples, worldX0, worldY0, worldZ0);
            CarveBlocks(blocks, column, worldY0, samples);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }

    // Lattice points sit on world coordinates that are multiples of Stride, so adjacent chunks
    // sample identical values along their shared face
    private static void SampleLattice(float[] samples, int worldX0, int worldY0, int worldZ0)
    {
        for (var gx = 0; gx < Grid; gx++)
        {
            var wx = worldX0 + gx * Stride;
            for (var gy = 0; gy < Grid; gy++)
            {
                var wy = worldY0 + gy * Stride;
                for (var gz = 0; gz < Grid; gz++)
                {
                    var wz = worldZ0 + gz * Stride;
                    var g = gx * GridSq + gy * Grid + gz;

                    samples[ChCheese * GridCount + g] = Cheese.GetNoise(wx, wy * WorldGenerationConfig.CheeseYScale, wz);
                    samples[ChSpaghettiA * GridCount + g] = SpaghettiA.GetNoise(wx, wy * WorldGenerationConfig.SpaghettiYScale, wz);
                    samples[ChSpaghettiB * GridCount + g] = SpaghettiB.GetNoise(wx, wy * WorldGenerationConfig.SpaghettiYScale, wz);
                    samples[ChThickness * GridCount + g] = Thickness.GetNoise(wx, wy, wz);
                    samples[ChNarrowA * GridCount + g] = NarrowA.GetNoise(wx, wy * WorldGenerationConfig.SpaghettiYScale, wz);
                    samples[ChNarrowB * GridCount + g] = NarrowB.GetNoise(wx, wy * WorldGenerationConfig.SpaghettiYScale, wz);
                    samples[ChCavern * GridCount + g] = Cavern.GetNoise(wx, wy * WorldGenerationConfig.CavernYScale, wz);
                    samples[ChNarrowMask * GridCount + g] = NarrowMask.GetNoise(wx, wy, wz);
                }
            }
        }
    }

    private static void CarveBlocks(byte[] blocks, ColumnData column, int worldY0, float[] samples)
    {
        for (var x = 0; x < Chunk.Size; x++)
        {
            var gx = x / Stride;
            var fx = ((x % Stride) + 0.5f) * InvStride;

            for (var z = 0; z < Chunk.Size; z++)
            {
                var col = (x << Chunk.SizeShift) | z;
                var height = column.Heights[col];
                var entrance = column.EntranceWeight[col];
                var intensity = column.CaveIntensity[col];

                var gz = z / Stride;
                var fz = ((z % Stride) + 0.5f) * InvStride;

                for (var y = 0; y < Chunk.Size; y++)
                {
                    var depth = height - (worldY0 + y);
                    if (depth < 0) break; // above the surface: already air

                    var idx = Chunk.Idx(x, y, z);
                    if (blocks[idx] == BlockType.Air) continue;

                    // Surface gate: caves close up near the surface unless this is an entrance zone
                    var gate = MathF.Max(
                        MathUtils.SmoothStep(WorldGenerationConfig.CaveSurfaceFadeStart, WorldGenerationConfig.CaveSurfaceFadeEnd, depth),
                        entrance);
                    if (gate <= 0f) continue;

                    var gy = y / Stride;
                    var fy = ((y % Stride) + 0.5f) * InvStride;
                    var g = gx * GridSq + gy * Grid + gz;

                    if (IsCave(samples, g, fx, fy, fz, depth, gate, intensity))
                        blocks[idx] = BlockType.Air;
                }
            }
        }
    }

    private static bool IsCave(float[] samples, int g, float fx, float fy, float fz, float depth, float gate, float intensity)
    {
        // Cheese rooms: threshold falls with depth so rooms grow; the gate term closes them near the surface
        var cheese = Trilinear(samples, ChCheese * GridCount + g, fx, fy, fz);
        var cheeseThreshold = MathUtils.Lerp(
            WorldGenerationConfig.CheeseThresholdShallow,
            WorldGenerationConfig.CheeseThresholdDeep,
            MathUtils.SmoothStep(WorldGenerationConfig.CheeseDepthStart, WorldGenerationConfig.CheeseDepthEnd, depth));
        if (cheese > cheeseThreshold - intensity + (1f - gate))
            return true;

        // Wide spaghetti: inside the tube around the curve where both fields are zero
        var a = Trilinear(samples, ChSpaghettiA * GridCount + g, fx, fy, fz);
        var b = Trilinear(samples, ChSpaghettiB * GridCount + g, fx, fy, fz);
        var thick = Trilinear(samples, ChThickness * GridCount + g, fx, fy, fz);
        var radius = gate * (WorldGenerationConfig.SpaghettiRadius + WorldGenerationConfig.SpaghettiRadiusVariation * thick + intensity * 0.5f);
        if (a * a + b * b < radius * radius)
            return true;

        // Narrow spaghetti, only inside patches of the mask field
        var narrowMask = MathUtils.SmoothStep(
            WorldGenerationConfig.NarrowSpaghettiMaskStart,
            WorldGenerationConfig.NarrowSpaghettiMaskEnd,
            Trilinear(samples, ChNarrowMask * GridCount + g, fx, fy, fz));
        if (narrowMask > 0f)
        {
            a = Trilinear(samples, ChNarrowA * GridCount + g, fx, fy, fz);
            b = Trilinear(samples, ChNarrowB * GridCount + g, fx, fy, fz);
            radius = gate * narrowMask * (WorldGenerationConfig.NarrowSpaghettiRadius + intensity * 0.25f);
            if (a * a + b * b < radius * radius)
                return true;
        }

        // Deep caverns: the depth ramp scales the field down so they cannot exist shallow
        var cavernScale = MathUtils.SmoothStep(WorldGenerationConfig.CavernDepthStart, WorldGenerationConfig.CavernDepthEnd, depth);
        if (cavernScale > 0f)
        {
            var cavern = Trilinear(samples, ChCavern * GridCount + g, fx, fy, fz);
            if (cavern * cavernScale > WorldGenerationConfig.CavernThreshold - intensity)
                return true;
        }

        return false;
    }

    /// <summary>Interpolates one channel at fractional lattice offset (fx, fy, fz) from lattice index base.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Trilinear(float[] s, int b, float fx, float fy, float fz)
    {
        var x0y0 = s[b] + (s[b + 1] - s[b]) * fz;
        var x0y1 = s[b + Grid] + (s[b + Grid + 1] - s[b + Grid]) * fz;
        var x1y0 = s[b + GridSq] + (s[b + GridSq + 1] - s[b + GridSq]) * fz;
        var x1y1 = s[b + GridSq + Grid] + (s[b + GridSq + Grid + 1] - s[b + GridSq + Grid]) * fz;

        var x0 = x0y0 + (x0y1 - x0y0) * fy;
        var x1 = x1y0 + (x1y1 - x1y0) * fy;
        return x0 + (x1 - x0) * fx;
    }
}
