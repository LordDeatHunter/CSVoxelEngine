using System;
using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The clumped-plant rule (<see cref="PlantClumps"/> over a <see cref="PlantClumpSpec"/>), tested
/// on the pattern itself, before any terrain can reject a member: every clump keeps its guaranteed
/// core, members stay inside the disc and thin toward its rim, centres keep their cells and their
/// spacing, the seed alone decides the result, and a clump crossing a cell or chunk border is the
/// same clump seen from either side.
/// </summary>
public sealed class PlantClumpTests
{
    private const int SeedA = WorldSeed.Default;
    private const int SeedB = WorldSeed.Default + 7919;

    // The shipped cotton shape plus two loose ones: a clump that can cross its cell border (radius
    // past the cell margin, wide jitter) and a tiny-cell, single-member extreme.
    public static TheoryData<string> Specs => new() { "cotton", "loose", "tiny" };

    private static PlantClumpSpec SpecNamed(string name) => name switch
    {
        "cotton" => CottonGenerator.Clumps,
        "loose" => new PlantClumpSpec(cellShift: 4, radius: 5, jitter: 7, minMembers: 9, chance: 0.8f,
            edgeDensity: 0.3f, salt: 0x51A7),
        _ => new PlantClumpSpec(cellShift: 1, radius: 0, jitter: 0, minMembers: 1, chance: 0.5f,
            edgeDensity: 0f, salt: 0x51A8),
    };

    public PlantClumpTests() => TestContent.EnsureLoaded();

    /// <summary>Every accepted clump in a block of cells around the origin, with its centre.</summary>
    private static List<(int CellX, int CellZ, int X, int Z)> Clumps(PlantClumpSpec spec, int seed, int cells = 24)
    {
        var found = new List<(int, int, int, int)>();
        for (var cx = -cells; cx < cells; cx++)
        for (var cz = -cells; cz < cells; cz++)
            if (PlantClumps.TryGetClump(spec, seed, cx, cz, out var x, out var z))
                found.Add((cx, cz, x, z));
        Assert.NotEmpty(found);
        return found;
    }

    [Theory]
    [MemberData(nameof(Specs))]
    public void EveryClumpHasAtLeastTheMinimumMembersAllWithinTheRadius(string name)
    {
        var spec = SpecNamed(name);
        var r = spec.Radius;
        foreach (var (_, _, cx, cz) in Clumps(spec, SeedA))
        {
            var members = 0;
            // Scan past the disc so a member outside it would be caught
            for (var dx = -r - 2; dx <= r + 2; dx++)
            for (var dz = -r - 2; dz <= r + 2; dz++)
            {
                if (!PlantClumps.IsMember(spec, SeedA, cx, cz, cx + dx, cz + dz)) continue;
                members++;
                Assert.True(dx * dx + dz * dz <= r * r, $"{name}: member ({dx}, {dz}) lies outside radius {r}");
            }

            Assert.True(members >= spec.MinMembers, $"{name}: clump at ({cx}, {cz}) has {members} < {spec.MinMembers} members");
        }
    }

    [Theory]
    [MemberData(nameof(Specs))]
    public void AColumnIsOnlyReportedAsAMemberOfAClumpWithinTheRadius(string name)
    {
        var spec = SpecNamed(name);
        var reported = 0;
        for (var x = -200; x < 200; x++)
        for (var z = -60; z < 60; z++)
        {
            if (!PlantClumps.TryGetMember(spec, SeedA, x, z, null, out var cx, out var cz)) continue;
            reported++;
            Assert.True(PlantClumps.TryGetClump(spec, SeedA, cx >> spec.CellShift, cz >> spec.CellShift, out var ax, out var az)
                        && (ax, az) == (cx, cz), $"{name}: ({x}, {z}) reports a centre no cell holds");
            var dx = x - cx;
            var dz = z - cz;
            Assert.True(dx * dx + dz * dz <= spec.Radius * spec.Radius, $"{name}: ({x}, {z}) is {dx},{dz} off its centre");
        }

        Assert.True(reported > 0, $"{name}: no member in the sampled area");
    }

    [Theory]
    [MemberData(nameof(Specs))]
    public void ClumpCentresStayInTheirCellAndKeepTheStatedSpacing(string name)
    {
        var spec = SpecNamed(name);
        var clumps = Clumps(spec, SeedA, cells: 16);
        foreach (var (cellX, cellZ, x, z) in clumps)
            Assert.Equal((cellX, cellZ), (x >> spec.CellShift, z >> spec.CellShift));

        // MinCentreSpacing is the real bound with jitter; the cell size only without it
        Assert.True(spec.MinCentreSpacing >= 1);
        Assert.True(spec.MinCentreSpacing <= spec.CellSize);
        for (var i = 0; i < clumps.Count; i++)
        for (var j = i + 1; j < clumps.Count; j++)
        {
            var spacing = Math.Max(Math.Abs(clumps[i].X - clumps[j].X), Math.Abs(clumps[i].Z - clumps[j].Z));
            Assert.True(spacing >= spec.MinCentreSpacing,
                $"{name}: centres ({clumps[i].X}, {clumps[i].Z}) and ({clumps[j].X}, {clumps[j].Z}) are {spacing} apart");
        }
    }

    [Fact]
    public void CottonClumpsNeverOverlap()
    {
        // Two discs of the shipped spec can only touch if their centres come within 2 * radius
        var spec = CottonGenerator.Clumps;
        Assert.True(spec.MinCentreSpacing > 2 * spec.Radius,
            $"cotton centres may come {spec.MinCentreSpacing} apart, so two radius-{spec.Radius} clumps can merge");
    }

    [Theory]
    [MemberData(nameof(Specs))]
    public void TheSameSeedGivesTheSameClumpsAndAnotherSeedOthers(string name)
    {
        var spec = SpecNamed(name);
        Assert.Equal(Clumps(spec, SeedA), Clumps(spec, SeedA));
        Assert.NotEqual(Clumps(spec, SeedA), Clumps(spec, SeedB));

        // Membership too, column by column
        for (var x = -40; x < 40; x++)
        for (var z = -40; z < 40; z++)
        {
            var first = PlantClumps.TryGetMember(spec, SeedA, x, z, null, out var ax, out var az);
            var second = PlantClumps.TryGetMember(spec, SeedA, x, z, null, out var bx, out var bz);
            Assert.Equal((first, ax, az), (second, bx, bz));
        }
    }

    [Fact]
    public void AClumpStraddlingACellBorderHasMembersOnBothSides()
    {
        // The loose spec reaches past its cell, so its far-side members exist only because a
        // column asks its neighbouring cells as well
        var spec = SpecNamed("loose");
        var checkedClumps = 0;
        foreach (var (cellX, cellZ, cx, cz) in Clumps(spec, SeedA))
        {
            var own = 0;
            var across = 0;
            for (var dx = -spec.Radius; dx <= spec.Radius; dx++)
            for (var dz = -spec.Radius; dz <= spec.Radius; dz++)
            {
                var x = cx + dx;
                var z = cz + dz;
                if (!PlantClumps.IsMember(spec, SeedA, cx, cz, x, z)) continue;
                if ((x >> spec.CellShift, z >> spec.CellShift) == (cellX, cellZ)) own++;
                else
                {
                    across++;
                    // Asked from the neighbouring cell's side, the column is still this clump's
                    // (the loose spec can overlap, so another clump may answer first; it must
                    // still answer)
                    Assert.True(PlantClumps.TryGetMember(spec, SeedA, x, z, null, out _, out _),
                        $"({x}, {z}) across the border of cell ({cellX}, {cellZ}) lost its clump");
                }
            }

            if (own > 0 && across > 0) checkedClumps++;
        }

        Assert.True(checkedClumps > 0, "expected a loose clump with members on both sides of its cell border");
    }

    [Fact]
    public void ACottonClumpOnAChunkBorderIsNotCutInHalf()
    {
        // Chunks stamp independently, so a clump that straddles a chunk line must report the same
        // centre to the columns of both chunks
        var spec = CottonGenerator.Clumps;
        var straddling = 0;
        foreach (var (_, _, cx, cz) in Clumps(spec, SeedA, cells: 32))
        {
            var sides = new HashSet<int>();
            for (var dx = -spec.Radius; dx <= spec.Radius; dx++)
            for (var dz = -spec.Radius; dz <= spec.Radius; dz++)
            {
                var x = cx + dx;
                var z = cz + dz;
                if (!PlantClumps.IsMember(spec, SeedA, cx, cz, x, z)) continue;
                Assert.True(PlantClumps.TryGetMember(spec, SeedA, x, z, null, out var mx, out var mz));
                Assert.Equal((cx, cz), (mx, mz));
                sides.Add(x >> Chunk.SizeShift);
            }

            if (sides.Count > 1) straddling++;
        }

        Assert.True(straddling > 0, "expected a cotton clump with members in two chunk columns");
    }

    // Not the tiny spec: a zero-radius disc has no inside and outside to compare
    [Theory]
    [InlineData("cotton")]
    [InlineData("loose")]
    public void MembersThinTowardTheRim(string name)
    {
        var spec = SpecNamed(name);

        // Share of the disc's cells taken, inside half the radius against the outer half
        long innerTaken = 0, innerCells = 0, outerTaken = 0, outerCells = 0;
        var r = spec.Radius;
        var half2 = r * r / 4f;
        foreach (var (_, _, cx, cz) in Clumps(spec, SeedA, cells: 40))
        {
            for (var dx = -r; dx <= r; dx++)
            for (var dz = -r; dz <= r; dz++)
            {
                var d2 = dx * dx + dz * dz;
                if (d2 > r * r) continue;
                var member = PlantClumps.IsMember(spec, SeedA, cx, cz, cx + dx, cz + dz);
                if (d2 <= half2)
                {
                    innerCells++;
                    if (member) innerTaken++;
                }
                else
                {
                    outerCells++;
                    if (member) outerTaken++;
                }
            }
        }

        Assert.True((double)innerTaken / innerCells > (double)outerTaken / outerCells,
            $"{name}: inner share {innerTaken}/{innerCells} is not denser than outer {outerTaken}/{outerCells}");
    }

    [Fact]
    public void ASpecThatCouldBreakItsGuaranteesIsRefused()
    {
        // A centre jittered out of its cell would let two clumps share a cell
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlantClumpSpec(cellShift: 4, radius: 3, jitter: 8, minMembers: 1, chance: 1f, edgeDensity: 0f, salt: 1));
        // More guaranteed members than the disc holds could never be honoured
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlantClumpSpec(cellShift: 4, radius: 1, jitter: 0, minMembers: 6, chance: 1f, edgeDensity: 0f, salt: 1));
        // The guaranteed core always holds at least the minimum
        var spec = new PlantClumpSpec(cellShift: 4, radius: 3, jitter: 0, minMembers: 6, chance: 1f, edgeDensity: 0f, salt: 1);
        var core = 0;
        for (var dx = -3; dx <= 3; dx++)
        for (var dz = -3; dz <= 3; dz++)
            if (dx * dx + dz * dz <= spec.CoreRadiusSquared) core++;
        Assert.True(core >= spec.MinMembers);
    }
}
