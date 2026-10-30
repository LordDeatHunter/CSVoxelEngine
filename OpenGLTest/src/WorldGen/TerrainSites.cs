using System;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The shared hashed-site rule behind every "one candidate per grid cell" terrain feature (lakes,
/// massifs, lone peaks): which cells can reach a point, the per-cell roll (chance, a center inside the
/// cell, a skewed size), the outline wobble, the rim samples a site judges its ground by and the
/// lowest-hash spacing between neighbors. GL-free and pure: every value is a function of the cell, the
/// salt and <see cref="WorldGenerationConfig.Seed"/>, so it is safe on any worker thread.
/// </summary>
internal static class TerrainSites
{
    /// <summary>The number of rim points <see cref="RimPoint"/> spreads evenly round a site.</summary>
    internal const int RimPoints = 8;

    /// <summary>
    /// The inclusive range of cells along one axis whose candidates can reach coordinate
    /// <paramref name="v"/>: a candidate's center lies inside its cell and reaches at most
    /// <paramref name="reach"/> blocks, so every cell outside this range is too far (a block of margin
    /// on each side absorbs the rounding of a center at the cell's edge).
    /// </summary>
    internal static void CellRange(float v, int cellBlocks, float reach, out int min, out int max)
    {
        min = (int)MathF.Floor((v - reach - 1f) / cellBlocks);
        max = (int)MathF.Floor((v + reach + 1f) / cellBlocks);
    }

    /// <summary>
    /// The candidate of cell (<paramref name="gx"/>, <paramref name="gz"/>): false when the cell's roll
    /// misses <paramref name="chance"/> (every out zeroed), else its hash, a center inside the cell and a
    /// 0..1 size skewed by <paramref name="sizeSkew"/> toward small. The hash is salted so each feature
    /// rolls its own stream, and further values of the same candidate are read with
    /// <see cref="SplitMix64.Unit"/> from index 4 on.
    /// </summary>
    internal static bool TryRoll(int gx, int gz, int cellBlocks, int salt, float chance, float sizeSkew,
        out ulong hash, out float centerX, out float centerZ, out float size)
    {
        hash = SplitMix64.Hash(gx, gz, WorldGenerationConfig.Seed ^ salt);
        if (SplitMix64.Unit(hash, 0) >= chance)
        {
            hash = 0;
            centerX = 0f;
            centerZ = 0f;
            size = 0f;
            return false;
        }

        centerX = gx * cellBlocks + SplitMix64.Unit(hash, 1) * cellBlocks;
        centerZ = gz * cellBlocks + SplitMix64.Unit(hash, 2) * cellBlocks;
        size = MathF.Pow(SplitMix64.Unit(hash, 3), sizeSkew);
        return true;
    }

    /// <summary>
    /// A site's outline radius at (x, z): <paramref name="nominal"/> moved in and out by up to
    /// <paramref name="amplitude"/> of itself by the noise, so the outline is ragged rather than a circle.
    /// It can exceed the nominal radius; a caller culling at the nominal radius clips it there.
    /// </summary>
    internal static float Wobble(float nominal, float amplitude, FastNoiseLite noise, float x, float z) =>
        nominal * (1f + amplitude * noise.GetNoise(x, z));

    /// <summary>
    /// A site's outline radius at (x, z), pulled only inward: between <paramref name="nominal"/> times
    /// (1 - <paramref name="amplitude"/>) and <paramref name="nominal"/> itself. Never above the nominal
    /// radius, so a term that falls to zero at this radius is already zero where a caller culls at the
    /// nominal one, and the cull leaves no step.
    /// </summary>
    internal static float InwardWobble(float nominal, float amplitude, FastNoiseLite noise, float x, float z) =>
        InwardWobbleOf(nominal, amplitude, noise.GetNoise(x, z));

    /// <summary>
    /// <see cref="InwardWobble"/> from an already read noise value (-1..1), so a caller that tests several
    /// outlines at one point (a great lake's lobes) reads the noise once.
    /// </summary>
    internal static float InwardWobbleOf(float nominal, float amplitude, float noiseValue) =>
        nominal * (1f - amplitude * (0.5f + 0.5f * noiseValue));

    /// <summary>
    /// Whether <paramref name="test"/> holds at any point of the circle of <paramref name="radius"/> round
    /// the center, sampled every <paramref name="spacing"/> blocks of arc starting on +X; a radius of 0 or
    /// less tests the center alone. Pass a static, non-capturing delegate field so a hot caller allocates
    /// nothing.
    /// </summary>
    internal static bool AnyOnRing(float centerX, float centerZ, float radius, float spacing,
        Func<float, float, bool> test)
    {
        if (radius <= 0f) return test(centerX, centerZ);

        var points = Math.Max(1, (int)MathF.Ceiling(MathF.Tau * radius / spacing));
        for (var k = 0; k < points; k++)
        {
            var angle = k * (MathF.Tau / points);
            if (test(centerX + MathF.Cos(angle) * radius, centerZ + MathF.Sin(angle) * radius)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="test"/> holds anywhere in the annulus from <paramref name="rIn"/> to
    /// <paramref name="rOut"/> round the center: <see cref="AnyOnRing"/> over evenly spaced rings at most
    /// <paramref name="spacing"/> apart, both edges included. Pass a static, non-capturing delegate field
    /// so a hot caller allocates nothing.
    /// </summary>
    internal static bool AnyInAnnulus(float centerX, float centerZ, float rIn, float rOut, float spacing,
        Func<float, float, bool> test)
    {
        var rings = Math.Max(1, (int)MathF.Ceiling((rOut - rIn) / spacing));
        for (var ring = 0; ring <= rings; ring++)
            if (AnyOnRing(centerX, centerZ, rIn + (rOut - rIn) * ring / rings, spacing, test))
                return true;

        return false;
    }

    /// <summary>The <paramref name="k"/>-th of <see cref="RimPoints"/> points evenly spaced on the circle of
    /// <paramref name="radius"/> round the center, starting on +X.</summary>
    internal static void RimPoint(float centerX, float centerZ, float radius, int k, out float x, out float z)
    {
        var angle = k * (MathF.Tau / 8f);
        x = centerX + MathF.Cos(angle) * radius;
        z = centerZ + MathF.Sin(angle) * radius;
    }

    /// <summary>
    /// The lowest and highest of <paramref name="heightAt"/> over a site's center and its
    /// <see cref="RimPoints"/> rim points: the spread a site judges whether its ground is flat enough
    /// by. Pass a static, non-capturing delegate field so a hot caller allocates nothing.
    /// </summary>
    internal static void RimRange(float centerX, float centerZ, float radius, Func<float, float, float> heightAt,
        out float min, out float max)
    {
        min = heightAt(centerX, centerZ);
        max = min;
        for (var k = 0; k < RimPoints; k++)
        {
            RimPoint(centerX, centerZ, radius, k, out var x, out var z);
            var h = heightAt(x, z);
            if (h < min) min = h;
            if (h > max) max = h;
        }
    }

    /// <summary>
    /// Whether the candidate of cell (<paramref name="gx"/>, <paramref name="gz"/>) gives way to a
    /// neighbor: true when another cell's successful roll (same salt, chance and skew) has a lower hash
    /// and a center closer than <paramref name="spacing"/>. The lowest hash wins, so of two crowding
    /// candidates exactly one survives whichever is asked; the test is on raw rolls, so a neighbor that
    /// is later rejected for another reason can still suppress a site.
    /// </summary>
    internal static bool YieldsToNeighbor(int gx, int gz, ulong hash, float centerX, float centerZ, float spacing,
        int cellBlocks, int salt, float chance, float sizeSkew)
    {
        CellRange(centerX, cellBlocks, spacing, out var minX, out var maxX);
        CellRange(centerZ, cellBlocks, spacing, out var minZ, out var maxZ);
        var spacing2 = spacing * spacing;
        for (var ox = minX; ox <= maxX; ox++)
        for (var oz = minZ; oz <= maxZ; oz++)
        {
            if (ox == gx && oz == gz) continue;
            if (!TryRoll(ox, oz, cellBlocks, salt, chance, sizeSkew, out var otherHash, out var otherX,
                    out var otherZ, out _))
                continue;
            if (otherHash >= hash) continue;

            var dx = otherX - centerX;
            var dz = otherZ - centerZ;
            if (dx * dx + dz * dz < spacing2) return true;
        }

        return false;
    }
}
