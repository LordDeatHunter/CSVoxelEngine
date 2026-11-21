using System;

namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>Pure cone and owned caldera geometry. The later deep conduit extends this classifier.</summary>
internal static class VolcanoShape
{
    internal static float Distance(VolcanoSite site, float x, float z) =>
        MathF.Sqrt((x - site.X) * (x - site.X) + (z - site.Z) * (z - site.Z));

    internal static bool InCrater(VolcanoSite site, float x, float z) =>
        site.Exists && Distance(site, x, z) < site.CraterRadius;

    internal static bool InGuard(VolcanoSite site, float x, float z) =>
        site.Exists && Distance(site, x, z) < site.CraterRadius + WorldGenerationConfig.VolcanoGuard;

    internal static bool InLavaDisc(VolcanoSite site, float x, float z) =>
        site.Exists && Distance(site, x, z) < site.CraterRadius - WorldGenerationConfig.VolcanoLiningThickness;

    /// <summary>Exact identity outside the nominal disc. The crater floor is an assignment;
    /// the wall rises into a soft-ceiled shoulder and the cone reaches zero at its edge.</summary>
    internal static float HeightAt(VolcanoSite site, float x, float z, float input)
    {
        if (!site.Exists) return input;
        var r = Distance(site, x, z);
        if (r >= site.Radius) return input;
        if (r < site.CraterRadius) return site.Floor;

        var t = r / site.Radius;
        var body = MathF.Pow(1f - t * t, 1.5f);
        var shoulder = MathF.Max(input, TerrainPeaks.DynamicSoftCeiling(input + site.Rise * body));
        var wallT = (r - site.CraterRadius) / WorldGenerationConfig.VolcanoWallWidth;
        if (wallT >= 1f) return shoulder;
        return MathUtils.Lerp(site.Floor, shoulder, MathUtils.SmoothStep(wallT));
    }

    /// <summary>Classify the actual final-height caldera column. Its two-cell wall and bed close
    /// every horizontal and downward neighbor of a lava cell.</summary>
    internal static VolcanoCell Classify(VolcanoSite site, int x, int y, int z, int finalHeight)
    {
        if (!InCrater(site, x, z) || finalHeight != site.Floor) return VolcanoCell.None;
        var bottom = finalHeight - site.LakeDepth - WorldGenerationConfig.VolcanoLiningThickness;
        if (y < bottom || y > finalHeight) return VolcanoCell.None;
        return InLavaDisc(site, x, z) && y > finalHeight - site.LakeDepth
            ? VolcanoCell.Lava : VolcanoCell.Lining;
    }
}
