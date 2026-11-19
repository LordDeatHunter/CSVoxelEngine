using System;

namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>Pure cone, caldera and integer deep-volume geometry.</summary>
internal static class VolcanoShape
{
    private const int CenterY = WorldGenerationConfig.VolcanoChamberCenterY;
    private const int Radius = WorldGenerationConfig.VolcanoChamberRadius;
    private const int HalfHeight = WorldGenerationConfig.VolcanoChamberHalfHeight;
    private const int Shell = WorldGenerationConfig.VolcanoChamberLining;
    private static readonly int[] ShellRadii = BuildShellRadii();

    internal static int ChamberBottom => CenterY - (HalfHeight - 1) - Shell;
    internal static int ChamberTop => CenterY + (HalfHeight - 1) + Shell;
    internal static int ChamberReach => ShellRadii[HalfHeight + Shell];
    internal static int ConduitReach => WorldGenerationConfig.VolcanoConduitRadius
        + WorldGenerationConfig.VolcanoConduitLining;

    // The strict ellipsoid inequality is represented exactly by an integer squared-radius
    // limit. A precomputed per-height shell radius keeps integer square roots out of fill loops.
    private static long InteriorLimit(int dy)
    {
        var vertical = (long)HalfHeight * HalfHeight - (long)dy * dy;
        return vertical <= 0 ? -1 : ((long)Radius * Radius * vertical - 1)
            / ((long)HalfHeight * HalfHeight);
    }

    private static int[] BuildShellRadii()
    {
        var radii = new int[2 * (HalfHeight + Shell) + 1];
        for (var dy = -HalfHeight - Shell; dy <= HalfHeight + Shell; dy++)
        {
            var limit = -1L;
            for (var k = -Shell; k <= Shell; k++)
                limit = Math.Max(limit, InteriorLimit(dy + k));
            if (limit < 0) continue;
            var radius = (long)Math.Sqrt(limit);
            while ((radius + 1) * (radius + 1) <= limit) radius++;
            while (radius * radius > limit) radius--;
            radii[dy + HalfHeight + Shell] = checked((int)radius) + Shell;
        }
        return radii;
    }

    private static int ShellRadiusAt(int y)
    {
        var dy = (long)y - CenterY;
        return dy < -HalfHeight - Shell || dy > HalfHeight + Shell
            ? 0 : ShellRadii[(int)dy + HalfHeight + Shell];
    }

    internal static bool InDeepBox(VolcanoSite site, int minX, int minY, int minZ,
        int maxX, int maxY, int maxZ)
    {
        if (!site.Deep.Exists) return false;
        var ax = (long)site.Deep.AxisX;
        var az = (long)site.Deep.AxisZ;
        var chamber = maxY >= ChamberBottom && minY <= ChamberTop
            && minX <= ax + ChamberReach && maxX >= ax - ChamberReach
            && minZ <= az + ChamberReach && maxZ >= az - ChamberReach;
        var conduit = maxY >= WorldGenerationConfig.VolcanoChamberLavaSurfaceY
            && minY <= site.Floor - site.LakeDepth
            && minX <= ax + ConduitReach && maxX >= ax - ConduitReach
            && minZ <= az + ConduitReach && maxZ >= az - ConduitReach;
        return chamber || conduit;
    }
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

    /// <summary>Classify the connected source path, sealed chamber and old caldera. Deep
    /// ownership is explicit site data; no profile or column lookup enters this geometry.</summary>
    internal static VolcanoCell Classify(VolcanoSite site, int x, int y, int z, int finalHeight)
    {
        if (!site.Exists) return VolcanoCell.None;
        if (site.Deep.Exists)
        {
            var dx = (long)x - site.Deep.AxisX;
            var dz = (long)z - site.Deep.AxisZ;
            // Reject distant coordinates before squaring: even int endpoints can exceed long
            // when their difference is squared, while no deep cell reaches past this bound.
            if (dx < -ChamberReach || dx > ChamberReach || dz < -ChamberReach || dz > ChamberReach)
                return ClassifyCaldera(site, x, y, z, finalHeight);
            var squared = dx * dx + dz * dz;
            var core = (long)WorldGenerationConfig.VolcanoConduitRadius
                * WorldGenerationConfig.VolcanoConduitRadius;
            var ring = (long)ConduitReach * ConduitReach;
            var surface = WorldGenerationConfig.VolcanoChamberLavaSurfaceY;
            var conduitTop = site.Floor - site.LakeDepth;
            if (squared <= core && y >= surface && y <= conduitTop)
                return VolcanoCell.Lava;

            var caldera = y >= site.Floor - site.LakeDepth - WorldGenerationConfig.VolcanoLiningThickness
                ? ClassifyCaldera(site, x, y, z, finalHeight) : VolcanoCell.None;
            if (caldera != VolcanoCell.None) return caldera;

            var dy = (long)y - CenterY;
            var inside = dy > -HalfHeight && dy < HalfHeight
                && squared <= InteriorLimit((int)dy);
            if (inside && y <= surface) return VolcanoCell.Lava;
            if (squared > core && squared <= ring && y > surface && y <= conduitTop)
                return VolcanoCell.Lining;
            if (inside) return VolcanoCell.Air;
            var shellRadius = ShellRadiusAt(y);
            if (shellRadius > 0 && squared <= (long)shellRadius * shellRadius)
                return VolcanoCell.Lining;
            return VolcanoCell.None;
        }
        return ClassifyCaldera(site, x, y, z, finalHeight);
    }

    private static VolcanoCell ClassifyCaldera(VolcanoSite site, int x, int y, int z, int finalHeight)
    {
        if (!InCrater(site, x, z) || finalHeight != site.Floor) return VolcanoCell.None;
        var bottom = finalHeight - site.LakeDepth - WorldGenerationConfig.VolcanoLiningThickness;
        if (y < bottom || y > finalHeight) return VolcanoCell.None;
        return InLavaDisc(site, x, z) && y > finalHeight - site.LakeDepth
            ? VolcanoCell.Lava : VolcanoCell.Lining;
    }
}
