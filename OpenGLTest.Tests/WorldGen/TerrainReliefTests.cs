using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The relief layer (<see cref="TerrainRelief"/>): the highland weight is a unit weight that
/// vanishes around the spawn, every relief term is exactly neutral where that weight is 0, ranges
/// rise only inside highlands and run along their cell's direction, highlands stand above the
/// lowlands while the terrain stays under the cloud layer, and the range field crossfades between
/// cells without ever switching abruptly.
/// </summary>
public sealed class TerrainReliefTests
{
    public TerrainReliefTests() => TestContent.EnsureLoaded();

    private const int ScanHalfExtent = 16000;
    private const int ScanStride = 97;
    private const int OriginPatchHalfExtent = 60;

    private readonly record struct Sample(
        float X, float Z, float Weight, float Height, Biome Biome, float OceanWeight, float ForestHeight,
        float Mountain);

    private static readonly Lazy<Sample[]> Scan = new(BuildScan);

    private static Sample[] BuildScan()
    {
        var samples = new List<Sample>();
        for (var x = -ScanHalfExtent; x <= ScanHalfExtent; x += ScanStride)
        for (var z = -ScanHalfExtent; z <= ScanHalfExtent; z += ScanStride)
            samples.Add(SampleAt(x, z));
        for (var x = -OriginPatchHalfExtent; x <= OriginPatchHalfExtent; x++)
        for (var z = -OriginPatchHalfExtent; z <= OriginPatchHalfExtent; z++)
            samples.Add(SampleAt(x, z));
        return samples.ToArray();
    }

    private static Sample SampleAt(float x, float z)
    {
        var weight = TerrainRelief.HighlandWeight(x, z);
        var height = TerrainGenerator.LandHeight(x, z, out var biome, out var oceanWeight, out _,
            out var forestHeight);
        return new Sample(x, z, weight, height, biome, oceanWeight, forestHeight,
            TerrainRelief.MountainHeight(x, z, weight));
    }

    private static bool BitEqual(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    [Fact]
    public void TheHighlandWeightIsAUnitWeightThatVanishesAtTheSpawn()
    {
        var samples = Scan.Value;
        foreach (var s in samples)
        {
            Assert.True(s.Weight is >= 0f and <= 1f, $"highland weight {s.Weight} at ({s.X}, {s.Z}) is not in 0..1");
            if (TerrainRelief.SpawnClear(s.X, s.Z) == 0f)
                Assert.True(BitEqual(s.Weight, 0f),
                    $"highland weight {s.Weight} at ({s.X}, {s.Z}) inside the spawn clearing is not 0");
        }

        Assert.Contains(samples, s => s.Weight == 1f);
        Assert.Contains(samples, s => TerrainRelief.SpawnClear(s.X, s.Z) > 0f && s.Weight == 0f);
        Assert.Contains(samples, s => TerrainRelief.SpawnClear(s.X, s.Z) == 0f);
    }

    [Fact]
    public void EveryReliefTermIsNeutralWhereTheHighlandWeightIsZero()
    {
        Assert.True(BitEqual(TerrainRelief.HighlandLift(0f), 0f), "the lift of a lowland is not exactly 0");
        Assert.True(BitEqual(TerrainRelief.HillGain(0f), 1f), "the hill gain of a lowland is not exactly 1");

        var samples = Scan.Value;
        foreach (var s in samples.Where(s => s.Weight == 0f))
        {
            Assert.True(BitEqual(TerrainRelief.MountainHeight(s.X, s.Z, 0f), 0f),
                $"a range rises at lowland ({s.X}, {s.Z})");
            Assert.True(BitEqual(TerrainRelief.MountainHeight(s.X, s.Z, TerrainRelief.HighlandWeight(s.X, s.Z)), 0f),
                $"a range rises at lowland ({s.X}, {s.Z}) under its own highland weight");
        }

        Assert.Contains(samples, s => s.Weight > 0f && s.Mountain > 0f);
    }

    [Fact]
    public void MountainRangesRiseOnlyInsideHighlands()
    {
        var samples = Scan.Value;
        foreach (var s in samples.Where(s => s.Mountain > 0f))
            Assert.True(s.Weight > 0f, $"a range rises at ({s.X}, {s.Z}) outside every highland");

        Assert.Contains(samples, s => s.Mountain > 0f);
        var land = samples.Where(s => s.OceanWeight == 0f).ToArray();
        var withRange = land.Count(s => s.Mountain > 0f);
        var highland = land.Count(s => s.Weight > 0f);
        Assert.True(withRange < highland,
            $"{withRange} land samples carry a range, not fewer than the {highland} highland ones");
    }

    [Fact]
    public void HighlandsStandAboveTheLowlands()
    {
        var forest = Scan.Value.Where(s => s.Biome == Biome.Forest && s.OceanWeight == 0f).ToArray();
        var high = forest.Where(s => s.Weight == 1f).ToArray();
        var low = forest.Where(s => s.Weight == 0f).ToArray();
        Assert.NotEmpty(high);
        Assert.NotEmpty(low);

        var highMean = high.Average(s => s.ForestHeight);
        var lowMean = low.Average(s => s.ForestHeight);
        Assert.True(highMean > lowMean,
            $"forest highlands average {highMean} blocks, not above the lowlands' {lowMean}");
    }

    [Fact]
    public void TheTerrainStaysBelowTheCloudLayer()
    {
        var peak = Scan.Value.MaxBy(s => s.Height);
        Assert.True(peak.Height < SkyConfig.CloudHeight,
            $"the land at ({peak.X}, {peak.Z}) reaches {peak.Height}, into the cloud layer at {SkyConfig.CloudHeight}");
        Assert.True(peak.Weight > 0f || TerrainPeaks.MassifHeight(peak.X, peak.Z) > 0f
                || TerrainPeaks.LonePeakHeight(peak.X, peak.Z) > 0f,
            $"the highest land at ({peak.X}, {peak.Z}) is neither in a highland nor on a massif or a lone peak");
    }

    // A point (in already-warped coordinates) near the center of range cell (ci, cj), offset by
    // (a, b) cell fractions; the jittered cell lookup may still hand it to a neighbor.
    private static Vector2 CorePoint(int ci, int cj, float a, float b)
    {
        float cell = WorldGenerationConfig.MountainRangeCellBlocks;
        return new Vector2((ci + 0.5f + a) * cell, (cj + 0.5f + b) * cell);
    }

    // The single cell owning a point, or null when the point sits in a blend band on either axis.
    private static (int I, int J)? OwnerOf(Vector2 p)
    {
        var lattice = TerrainRelief.RangeLattice(p.X, p.Y);
        var u = lattice.X;
        var v = lattice.Y;
        var i0 = MathUtils.FloorToInt(u);
        var j0 = MathUtils.FloorToInt(v);
        var bx = TerrainRelief.RangeBlend(u - i0);
        var bz = TerrainRelief.RangeBlend(v - j0);
        if (bx != 0f && bx != 1f || bz != 0f && bz != 1f) return null;
        return (i0 + (bx == 1f ? 1 : 0), j0 + (bz == 1f ? 1 : 0));
    }

    [Fact]
    public void RangesRunAlongTheirCellsDirection()
    {
        const float step = 24f;
        float[] offsets = [-0.3f, -0.15f, 0f, 0.15f, 0.3f];
        var along = 0.0;
        var across = 0.0;
        var samples = 0;
        for (var ci = -8; ci <= 8; ci += 4)
        for (var cj = -8; cj <= 8; cj += 2)
        {
            foreach (var a in offsets)
            foreach (var b in offsets)
            {
                // The jittered lookup decides which cell owns the point; a sample whose probes do
                // not all sit in that one cell's unblended core is skipped
                var p = CorePoint(ci, cj, a, b);
                if (OwnerOf(p) is not { } owner) continue;
                var d = TerrainRelief.RangeDirection(owner.I, owner.J);
                var n = new Vector2(-d.Y, d.X);
                Vector2[] probes = [p + step * d, p - step * d, p + step * n, p - step * n];
                if (probes.Any(q => OwnerOf(q) != owner)) continue;

                var here = TerrainRelief.RangeShape(p.X, p.Y);
                var alongP = p + step * d;
                var acrossP = p + step * n;
                along += MathF.Abs(TerrainRelief.RangeShape(alongP.X, alongP.Y) - here);
                across += MathF.Abs(TerrainRelief.RangeShape(acrossP.X, acrossP.Y) - here);
                samples++;
            }
        }

        Assert.True(samples >= 40, $"only {samples} core samples");
        Assert.True(along < across,
            $"the range shape changes by {along} along the cells' directions, not less than {across} across them");
    }

    // The largest change of f between neighboring samples of a 0..1 sweep at the given resolution.
    private static float MaxStep(Func<float, float> f, int steps)
    {
        var max = 0f;
        var previous = f(0f);
        for (var k = 1; k <= steps; k++)
        {
            var value = f(k / (float)steps);
            max = MathF.Max(max, MathF.Abs(value - previous));
            previous = value;
        }

        return max;
    }

    [Fact]
    public void TheRangeFieldNeverSwitchesCellsAbruptly()
    {
        // (a) the blend is exactly 0 and 1 outside its band, never falls, and is continuous: a sweep
        // eight times finer shrinks its largest step, which a jump would keep at full size
        var bandStart = 0.5f - WorldGenerationConfig.MountainRangeBlend;
        var bandEnd = 0.5f + WorldGenerationConfig.MountainRangeBlend;
        var previous = TerrainRelief.RangeBlend(0f);
        for (var k = 0; k <= 256; k++)
        {
            var frac = k / 256f;
            var blend = TerrainRelief.RangeBlend(frac);
            if (frac <= bandStart) Assert.True(BitEqual(blend, 0f), $"the blend at fraction {frac} is {blend}, not 0");
            if (frac >= bandEnd) Assert.True(BitEqual(blend, 1f), $"the blend at fraction {frac} is {blend}, not 1");
            Assert.True(blend >= previous, $"the blend falls from {previous} to {blend} at fraction {frac}");
            previous = blend;
        }

        var coarse = MaxStep(TerrainRelief.RangeBlend, 256);
        var fine = MaxStep(TerrainRelief.RangeBlend, 2048);
        Assert.True(fine < coarse / 2f, $"the blend jumps: its largest step is {coarse} coarse and still {fine} fine");

        // (b) where the cell index changes (lattice u or v crossing an integer, a cell center) the
        // owning cell does not: just either side, the field is that one cell's shape exactly. The
        // lattice coordinates are set directly, since RangeLattice jitters the lookup by position
        // (continuously), and the point the cell shape reads is any point at all
        float cell = WorldGenerationConfig.MountainRangeCellBlocks;
        const float nudge = 1e-3f;
        for (var k = -6; k <= 6; k += 3)
        for (var j = -6; j <= 6; j += 4)
        {
            const float core = 0.2f;
            var x = (k + 0.5f) * cell + 37f;
            var z = (j + 0.5f + core) * cell - 11f;
            foreach (var side in new[] { -nudge, nudge })
            {
                Assert.True(BitEqual(TerrainRelief.RangeShapeAt(new Vector2(k + side, j + core), x, z),
                        TerrainRelief.CellShape(k, j, x, z)),
                    $"at lattice ({k + side}, {j + core}) beside a u index switch the field is not cell ({k}, {j}) alone");
                Assert.True(BitEqual(TerrainRelief.RangeShapeAt(new Vector2(j + core, k + side), z, x),
                        TerrainRelief.CellShape(j, k, z, x)),
                    $"at lattice ({j + core}, {k + side}) beside a v index switch the field is not cell ({j}, {k}) alone");
            }
        }

        // (c) inside a blend band the field is a convex combination of the contributing cells
        for (var i = -5; i <= 5; i += 2)
        for (var j = -5; j <= 5; j += 3)
        for (var t = 0; t <= 8; t++)
        {
            // u fraction across the band, v fraction in the band too on every other row
            var fracU = bandStart + (bandEnd - bandStart) * t / 8f;
            var fracV = (t & 1) == 0 ? 0.2f : 0.5f;
            var lattice = new Vector2(i + fracU, j + fracV);
            var x = (i + 0.5f + fracU) * cell;
            var z = (j + 0.5f + fracV) * cell;
            float[] contributors =
            [
                TerrainRelief.CellShape(i, j, x, z), TerrainRelief.CellShape(i + 1, j, x, z),
                TerrainRelief.CellShape(i, j + 1, x, z), TerrainRelief.CellShape(i + 1, j + 1, x, z),
            ];
            var shape = TerrainRelief.RangeShapeAt(lattice, x, z);
            const float epsilon = 1e-5f;
            Assert.True(shape >= contributors.Min() - epsilon && shape <= contributors.Max() + epsilon,
                $"the field {shape} at ({x}, {z}) leaves the range of its cells' shapes " +
                $"{contributors.Min()}..{contributors.Max()}");
        }
    }
}
