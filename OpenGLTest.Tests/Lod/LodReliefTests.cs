using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Lod;

/// <summary>
/// The far LOD over the steepest terrain the relief layer makes. Highlands lift the base, steepen
/// the hills and carry the mountain ranges, and the hashed massifs and lone peaks rise steeper still,
/// so the far shells meet far more relief than they did; the eroded window minimum minus the height
/// bias keeps every shell inside the terrain by construction, and these facts re-verify that over a
/// real highland range, a massif and a lone peak at every level, plus the nesting invariants that
/// make the levels bury each other and the ushort vertex budget the steeper side skirts eat into.
/// The islands rising out of deep water get the same checks over an island (with its summit's sky
/// exposure) and an archipelago, and their shores are held to leave no more dry pits in the far
/// shell than a continental coast does.
/// </summary>
[Collection("LOD column caps")]
public sealed class LodReliefTests
{
    // The searched box stays clear of the caps other tests in this collection place (level-1
    // (777, 777), level-2 (555, -555), level-0 rx 900..1100), which all lie at positive x.
    private const int SearchMin = -30000;
    private const int SearchMax = -2000;
    private const int SearchStride = 320;

    // The shore-pit fact compares totals over several regions per kind: one continental coast swings
    // from no pits to most of its shore-window cells, so a single region measures the region, not the
    // shore. A region counts once its shore spans this many shore-window cells.
    private const int PitRegionsPerKind = 6;
    private const int MinShoreWindowCells = 16;

    // A region over an island holds at least this many dry columns past the shore band
    private const int MinIslandDryColumns = 8;

    private readonly ITestOutputHelper _output;

    public LodReliefTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    [Fact]
    public void TheLodLevelsKeepTheirNestingInvariants()
    {
        Assert.True(LodRegion.Levels.Length > 1, "there is no pair of levels to nest");
        for (var level = 0; level + 1 < LodRegion.Levels.Length; level++)
        {
            var fine = LodRegion.Levels[level];
            var coarse = LodRegion.Levels[level + 1];
            Assert.True(coarse.SampleMargin >= fine.SampleMargin + fine.CellSize - 1,
                $"level {level + 1}'s sample margin {coarse.SampleMargin} does not cover level {level}'s " +
                $"margin {fine.SampleMargin} plus its cell {fine.CellSize} - 1");
            Assert.True(coarse.HeightBias >= fine.HeightBias + fine.CellSize,
                $"level {level + 1}'s height bias {coarse.HeightBias} does not sit a whole level-{level} " +
                $"cell ({fine.CellSize}) under its bias {fine.HeightBias}");
        }
    }

    [Fact]
    public void TheFarLodSitsInsideTheChunkTerrainOverAHighlandRange()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var (rx, rz) = FindHighlandRangeRegion(level);
            AssertLodInsideTerrain(level, rx, rz, "highland range");
        }
    }

    [Fact]
    public void TheFarLodSitsInsideTheChunkTerrainOverAMassif()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var (rx, rz) = FindSiteRegion(level, WorldGenerationConfig.MassifCellBlocks, TerrainPeaks.MassifSiteAt,
                minReliefCells: 4, "massif", summitInsideCells: 4);
            var mesh = AssertLodInsideTerrain(level, rx, rz, "massif");
            AssertTheSummitIsFullyExposed(level, rx, rz, mesh, "massif", terrainOnly: false);
        }
    }

    [Fact]
    public void TheFarLodSitsInsideTheChunkTerrainOverALonePeak()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var (rx, rz) = FindSiteRegion(level, WorldGenerationConfig.PeakCellBlocks, TerrainPeaks.PeakSiteAt,
                minReliefCells: 2, "lone peak");
            AssertLodInsideTerrain(level, rx, rz, "lone peak");
        }
    }

    [Fact]
    public void TheFarLodSitsInsideTheChunkTerrainOverAnIsland()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            // The first island summit region whose summit has a corner no crown stands over: a wooded
            // summit is rightly shaded by its own trees and says nothing about the terrain's exposure
            var found = false;
            foreach (var (rx, rz) in IslandSummitRegions(level))
            {
                var mesh = AssertLodInsideTerrain(level, rx, rz, "island");
                if (OpenSummitCorners(level, mesh, terrainOnly: true, out _, out _).Count == 0) continue;
                // The sky exposure re-verified over island relief: trees aside, nothing rises above its summit
                AssertTheSummitIsFullyExposed(level, rx, rz, mesh, "island", terrainOnly: true);
                found = true;
                break;
            }

            Assert.True(found, $"no level-{level} island summit region with an open summit corner in block box " +
                               $"x, z in [{SearchMin}, {SearchMax}]");
        }
    }

    [Fact]
    public void TheFarLodSitsInsideTheChunkTerrainOverAnArchipelago()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var (rx, rz) = FindArchipelagoRegion(level);
            AssertLodInsideTerrain(level, rx, rz, "archipelago");
        }
    }

    [Fact]
    public void IslandShoresLeaveNoMoreDryPitsInTheFarLodThanAContinentalCoast()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            var islandRegions = ShoreRegions(level, IslandRegions(level, archipelago: false), PitRegionsPerKind);
            var archipelagoRegion = ShoreRegions(level,
                IslandRegions(level, archipelago: true).Where(r => !islandRegions.Contains(r)), 1);
            islandRegions.AddRange(archipelagoRegion);
            var coastRegions = ShoreRegions(level, ContinentalCoastRegions(level), PitRegionsPerKind);

            // Positive controls: both kinds have shore to measure
            Assert.True(islandRegions.Count > 0, $"no level-{level} island region with a shore in the searched box");
            Assert.True(coastRegions.Count > 0, $"no level-{level} continental coast region in the searched box");

            var island = PitTotalsOver(level, islandRegions, "island");
            var coast = PitTotalsOver(level, coastRegions, "coast");
            Assert.True(island.ShoreWindowCells > 0 && coast.ShoreWindowCells > 0);

            _output.WriteLine($"level {level}: island pit share {island.PitShare:F3}, ring {island.RingWidth:F2} blocks " +
                              $"over {islandRegions.Count} regions; coast pit share {coast.PitShare:F3}, ring " +
                              $"{coast.RingWidth:F2} blocks over {coastRegions.Count} regions");

            // An island shore is steeper than a continental coast, so its far shell may sink below the
            // waterline over a wider band; it may not do so far more often. The slack (0.05 of the share,
            // one cell of ring width) absorbs sampling noise between a handful of regions.
            Assert.True(island.PitShare <= 2f * coast.PitShare + 0.05f,
                $"level {level}: island shores leave dry LOD pits in {island.PitShare:F3} of their shore-window " +
                $"cells, past twice a continental coast's {coast.PitShare:F3}");
            Assert.True(island.RingWidth <= 2f * coast.RingWidth + spec.CellSize,
                $"level {level}: the dry LOD ring around island shores is {island.RingWidth:F2} blocks wide, past " +
                $"twice a continental coast's {coast.RingWidth:F2} plus one cell");
        }
    }

    /// <summary>
    /// Builds the level-<paramref name="level"/> region (<paramref name="rx"/>, <paramref name="rz"/>)
    /// and checks that every dry, non-canopy up face stands at or below the lowest of the four chunk
    /// columns meeting at its corner, and that the build stays inside the vertex budget AddQuad keeps.
    /// </summary>
    private LodMeshData AssertLodInsideTerrain(int level, int rx, int rz, string what)
    {
        var spec = LodRegion.Levels[level];
        var mesh = LodRegion.BuildMeshData(level, rx, rz);

        // Positive control: an empty build would pass every check below
        Assert.NotEmpty(mesh.Vertices);

        var baseX = rx * spec.RegionBlocks;
        var baseZ = rz * spec.RegionBlocks;
        var checkedTops = 0;
        foreach (var v in mesh.Vertices)
        {
            if ((v.Normal & 7) != 4) continue;           // up faces only
            if (v.PackedColor >> 24 == 253) continue;    // a canopy bump stands above the ground
            var x = baseX + (int)v.Position.X;
            var z = baseZ + (int)v.Position.Z;
            if (!DryGroundMin(x, z, out var ground)) continue;

            checkedTops++;
            Assert.True(v.Position.Y <= ground,
                $"level {level} {what} region ({rx}, {rz}): the LOD top at ({x}, {z}) stands at {v.Position.Y}, " +
                $"above the lowest of its four columns ({ground})");
        }

        Assert.True(checkedTops > 0, $"level {level} {what} region ({rx}, {rz}) has no dry up face to check");

        // AddQuad refuses a quad once a list passes ushort.MaxValue - 3, silently dropping it
        Assert.True(mesh.Vertices.Count <= ushort.MaxValue - 7,
            $"level {level} {what} region ({rx}, {rz}) reached {mesh.Vertices.Count} vertices, where AddQuad drops quads");
        _output.WriteLine($"level {level} {what} region ({rx}, {rz}): {mesh.Vertices.Count} opaque vertices, " +
                          $"relief {RegionCeiling(level, rx, rz) - RegionFloor(level, rx, rz)} blocks");
        return mesh;
    }

    /// <summary>
    /// Nothing rises above a region's highest up face, so the sky exposure must not darken it: the
    /// up-face vertices at the greatest Y carry the brightest sun of every up face in the region. With
    /// <paramref name="terrainOnly"/> the canopy bumps (alpha 253) are left out of both, so a tree on
    /// the summit neither becomes the summit nor sets the brightest sun, and a summit corner a crown
    /// above the summit touches is skipped, since that crown does rise above it, as is one on the
    /// region's edge, whose crowns past the edge this mesh does not hold.
    /// </summary>
    private void AssertTheSummitIsFullyExposed(int level, int rx, int rz, LodMeshData mesh, string what, bool terrainOnly)
    {
        var summit = OpenSummitCorners(level, mesh, terrainOnly, out var topY, out var brightest);

        // Positive control: the region has up faces and some of them are lit at all
        Assert.True(topY > float.MinValue, $"level {level} {what} region ({rx}, {rz}) has no up face");
        Assert.True(brightest > 0, $"level {level} {what} region ({rx}, {rz}) has no lit up face");
        Assert.True(summit.Count > 0, $"level {level} {what} region ({rx}, {rz}) has no summit corner to check");

        foreach (var v in summit)
            Assert.True(v.Sun == brightest,
                $"level {level} {what} region ({rx}, {rz}): a summit vertex at ({v.Position.X}, {topY}, " +
                $"{v.Position.Z}) has sun {v.Sun}, darker than the brightest up face ({brightest})");

        _output.WriteLine($"level {level} {what} region ({rx}, {rz}): summit LOD top at Y {topY}, sun {brightest}, " +
                          $"{summit.Count} summit corners checked");
    }

    /// <summary>
    /// The up-face vertices at the region's greatest up-face Y (<paramref name="topY"/>) that
    /// <see cref="AssertTheSummitIsFullyExposed"/> checks, and the brightest sun of every up face. With
    /// <paramref name="terrainOnly"/> canopy bumps are left out, and so is a summit corner that a crown
    /// above the summit touches or that sits on the region's edge.
    /// </summary>
    private static List<Vertex> OpenSummitCorners(int level, LodMeshData mesh, bool terrainOnly, out float topY,
        out byte brightest)
    {
        topY = float.MinValue;
        brightest = 0;
        foreach (var v in mesh.Vertices)
        {
            if (!IsSummitCandidate(v, terrainOnly)) continue;
            topY = MathF.Max(topY, v.Position.Y);
            if (v.Sun > brightest) brightest = v.Sun;
        }

        var summit = new List<Vertex>();
        foreach (var v in mesh.Vertices)
        {
            if (!IsSummitCandidate(v, terrainOnly) || v.Position.Y != topY) continue;
            // A tree crown standing over the summit does rise above it: a corner it touches is rightly
            // shaded. A corner on the region's edge also reads the cells past it, whose crowns this mesh
            // does not draw, so it cannot be told apart.
            if (terrainOnly && (UnderACanopyAbove(mesh, v, topY) || OnRegionEdge(v, LodRegion.Levels[level]))) continue;
            summit.Add(v);
        }

        return summit;
    }

    private static bool IsSummitCandidate(Vertex v, bool terrainOnly) =>
        (v.Normal & 7) == 4 && (!terrainOnly || v.PackedColor >> 24 != 253);

    private static bool OnRegionEdge(Vertex v, LodLevelSpec spec) =>
        v.Position.X <= 0f || v.Position.X >= spec.RegionBlocks || v.Position.Z <= 0f || v.Position.Z >= spec.RegionBlocks;

    /// <summary>Whether a canopy bump's top quad standing above <paramref name="topY"/> touches the
    /// corner of <paramref name="v"/>: the exposure reads the three cells meeting at a corner, so such a
    /// crown is a column rising above the vertex.</summary>
    private static bool UnderACanopyAbove(LodMeshData mesh, Vertex v, float topY)
    {
        for (var q = 0; q + 3 < mesh.Vertices.Count; q += 4)
        {
            var corner = mesh.Vertices[q];
            if ((corner.Normal & 7) != 4 || corner.PackedColor >> 24 != 253 || corner.Position.Y <= topY) continue;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (var k = q; k < q + 4; k++)
            {
                var p = mesh.Vertices[k].Position;
                minX = MathF.Min(minX, p.X);
                maxX = MathF.Max(maxX, p.X);
                minZ = MathF.Min(minZ, p.Z);
                maxZ = MathF.Max(maxZ, p.Z);
            }

            if (v.Position.X >= minX && v.Position.X <= maxX && v.Position.Z >= minZ && v.Position.Z <= maxZ)
                return true;
        }

        return false;
    }

    private readonly record struct PitTotals(int ShoreWindowCells, int Pits, int ShorelineColumns, int CellSize)
    {
        public float PitShare => ShoreWindowCells == 0 ? 0f : Pits / (float)ShoreWindowCells;
        public float RingWidth => ShorelineColumns == 0 ? 0f : Pits * CellSize * CellSize / (float)ShorelineColumns;
    }

    /// <summary>Builds every region and sums its shore-pit counts, printing each region's own.</summary>
    private PitTotals PitTotalsOver(int level, List<(int Rx, int Rz)> regions, string what)
    {
        var spec = LodRegion.Levels[level];
        int shoreWindowCells = 0, pits = 0, shorelineColumns = 0;
        foreach (var (rx, rz) in regions)
        {
            var mesh = LodRegion.BuildMeshData(level, rx, rz);
            var (share, ring) = ShorePitStats(level, rx, rz, mesh, out var cells, out var regionPits, out var shoreline);
            _output.WriteLine($"level {level} {what} region ({rx}, {rz}): {regionPits} pits over {cells} shore-window " +
                              $"cells (share {share:F3}), {shoreline} shoreline columns (ring {ring:F2} blocks)");
            shoreWindowCells += cells;
            pits += regionPits;
            shorelineColumns += shoreline;
        }

        return new PitTotals(shoreWindowCells, pits, shorelineColumns, spec.CellSize);
    }

    /// <summary>
    /// The far LOD's dry pits along a region's shores. A level cell's window is its own fine footprint
    /// widened by the level's sample margin, the columns the eroded top reads. A shore-window cell's
    /// window holds both a wet column (water level above its height) and a dry one. A pit is a
    /// shore-window cell no water quad covers (its center inside the quad's XZ bounds) whose drawn
    /// opaque top (the highest non-canopy up quad over its center) sits more than a cell under the sea
    /// level, so the shell shows a sunken dry ring where the near terrain meets the water. Returns the
    /// pit share of the shore-window cells and the ring width in blocks: the pits' area over the
    /// shoreline columns (dry columns inside the region with a wet 4-neighbor).
    /// </summary>
    private static (float PitShare, float RingWidth) ShorePitStats(int level, int rx, int rz, LodMeshData mesh,
        out int shoreWindowCells, out int pits, out int shorelineColumns)
    {
        var spec = LodRegion.Levels[level];
        var cells = spec.Cells;
        var shore = ShoreWindowCells(level, rx, rz, out shoreWindowCells, out shorelineColumns);

        var top = new float[cells * cells];
        Array.Fill(top, float.MinValue);
        for (var q = 0; q + 3 < mesh.Vertices.Count; q += 4)
        {
            var corner = mesh.Vertices[q];
            if ((corner.Normal & 7) != 4 || corner.PackedColor >> 24 == 253) continue;
            QuadBounds(mesh.Vertices, q, spec.CellSize, cells, out var iMin, out var iMax, out var jMin, out var jMax,
                out var y);
            for (var i = iMin; i <= iMax; i++)
            for (var j = jMin; j <= jMax; j++)
                top[i * cells + j] = MathF.Max(top[i * cells + j], y);
        }

        var covered = new bool[cells * cells];
        for (var q = 0; q + 3 < mesh.WaterVertices.Count; q += 4)
        {
            QuadBounds(mesh.WaterVertices, q, spec.CellSize, cells, out var iMin, out var iMax, out var jMin, out var jMax,
                out _);
            for (var i = iMin; i <= iMax; i++)
            for (var j = jMin; j <= jMax; j++)
                covered[i * cells + j] = true;
        }

        pits = 0;
        for (var c = 0; c < cells * cells; c++)
            if (shore[c] && !covered[c] && top[c] < WorldGenerationConfig.SeaLevel - spec.CellSize)
                pits++;

        var totals = new PitTotals(shoreWindowCells, pits, shorelineColumns, spec.CellSize);
        return (totals.PitShare, totals.RingWidth);
    }

    /// <summary>The level cells whose centers lie inside the XZ bounds of the quad at
    /// <paramref name="first"/>, clamped to the region, and the quad's highest Y.</summary>
    private static void QuadBounds(List<Vertex> vertices, int first, int cellSize, int cells, out int iMin, out int iMax,
        out int jMin, out int jMax, out float y)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        y = float.MinValue;
        for (var k = first; k < first + 4; k++)
        {
            var p = vertices[k].Position;
            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
            y = MathF.Max(y, p.Y);
        }

        // Cell i's center sits at i * cellSize + cellSize / 2 in region-local blocks
        var half = cellSize / 2f;
        iMin = Math.Max(0, (int)MathF.Ceiling((minX - half) / cellSize));
        iMax = Math.Min(cells - 1, (int)MathF.Floor((maxX - half) / cellSize));
        jMin = Math.Max(0, (int)MathF.Ceiling((minZ - half) / cellSize));
        jMax = Math.Min(cells - 1, (int)MathF.Floor((maxZ - half) / cellSize));
    }

    /// <summary>
    /// Which of the region's level cells are shore-window cells (see <see cref="ShorePitStats"/>), how
    /// many there are, and how many shoreline columns the region holds.
    /// </summary>
    private static bool[] ShoreWindowCells(int level, int rx, int rz, out int shoreWindowCells, out int shorelineColumns)
    {
        var spec = LodRegion.Levels[level];
        var pad = spec.SampleMargin + 1; // the margin, plus one for a 4-neighbor of an edge column
        var size = spec.RegionBlocks + 2 * pad;
        var minX = rx * spec.RegionBlocks - pad;
        var minZ = rz * spec.RegionBlocks - pad;
        var wet = new bool[size * size];
        for (var gx = 0; gx < size; gx++)
        for (var gz = 0; gz < size; gz++)
        {
            var x = minX + gx;
            var z = minZ + gz;
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            wet[gx * size + gz] = column.WaterLevel[local] > column.Heights[local];
        }

        shorelineColumns = 0;
        for (var gx = pad; gx < pad + spec.RegionBlocks; gx++)
        for (var gz = pad; gz < pad + spec.RegionBlocks; gz++)
        {
            if (wet[gx * size + gz]) continue;
            if (wet[(gx - 1) * size + gz] || wet[(gx + 1) * size + gz] ||
                wet[gx * size + gz - 1] || wet[gx * size + gz + 1])
                shorelineColumns++;
        }

        var cells = spec.Cells;
        var shore = new bool[cells * cells];
        shoreWindowCells = 0;
        for (var i = 0; i < cells; i++)
        for (var j = 0; j < cells; j++)
        {
            bool anyWet = false, anyDry = false;
            var gx0 = pad + i * spec.CellSize - spec.SampleMargin;
            var gz0 = pad + j * spec.CellSize - spec.SampleMargin;
            var span = spec.CellSize + 2 * spec.SampleMargin;
            for (var gx = gx0; gx < gx0 + span; gx++)
            for (var gz = gz0; gz < gz0 + span; gz++)
            {
                if (wet[gx * size + gz]) anyWet = true;
                else anyDry = true;
            }

            if (!anyWet || !anyDry) continue;
            shore[i * cells + j] = true;
            shoreWindowCells++;
        }

        return shore;
    }

    /// <summary>The first <paramref name="count"/> of <paramref name="candidates"/> whose shores span at
    /// least <see cref="MinShoreWindowCells"/> shore-window cells.</summary>
    private static List<(int Rx, int Rz)> ShoreRegions(int level, IEnumerable<(int Rx, int Rz)> candidates, int count)
    {
        var regions = new List<(int Rx, int Rz)>();
        foreach (var (rx, rz) in candidates)
        {
            ShoreWindowCells(level, rx, rz, out var shoreWindowCells, out _);
            if (shoreWindowCells < MinShoreWindowCells) continue;
            regions.Add((rx, rz));
            if (regions.Count == count) break;
        }

        return regions;
    }

    /// <summary>
    /// The <see cref="IslandRegions"/> whose highest column (over the region and its border) is island
    /// land at least two level cells in from every region edge, standing more than two cells over the
    /// region's floor, so the region's highest LOD face is the island's own summit.
    /// </summary>
    private static IEnumerable<(int Rx, int Rz)> IslandSummitRegions(int level)
    {
        var spec = LodRegion.Levels[level];
        foreach (var (rx, rz) in IslandRegions(level, archipelago: false))
        {
            var (x, z, height) = HighestColumn(level, rx, rz);
            if (!InsideRegion(x, rx, spec, 2) || !InsideRegion(z, rz, spec, 2)) continue;
            if (BiomeClassifier.PreIslandOceanWeight(x, z) <= WorldGenerationConfig.OceanShoreWeight) continue;
            if (height - RegionFloor(level, rx, rz) <= 2 * spec.CellSize) continue;
            yield return (rx, rz);
        }
    }

    /// <summary>The first level-<paramref name="level"/> archipelago region of <see cref="IslandRegions"/>.</summary>
    private static (int Rx, int Rz) FindArchipelagoRegion(int level)
    {
        foreach (var region in IslandRegions(level, archipelago: true))
            return region;

        Assert.Fail($"no level-{level} archipelago region in block box x, z in [{SearchMin}, {SearchMax}]");
        return (0, 0);
    }

    /// <summary>
    /// Level-<paramref name="level"/> regions wholly inside the searched box over island land, cheapest
    /// check first: the region center lies past the shore band of the pre-island ocean and is island land
    /// (not the Ocean biome), or, for an <paramref name="archipelago"/>, deep inside an archipelago
    /// footprint; then the region and its 1-chunk border hold at least
    /// <see cref="MinIslandDryColumns"/> dry columns past the shore band.
    /// </summary>
    private static IEnumerable<(int Rx, int Rz)> IslandRegions(int level, bool archipelago)
    {
        var spec = LodRegion.Levels[level];
        for (var bx = SearchMax - spec.RegionBlocks; bx >= SearchMin; bx -= SearchStride)
        for (var bz = SearchMax - spec.RegionBlocks; bz >= SearchMin; bz -= SearchStride)
        {
            var rx = FloorDiv(bx, spec.RegionBlocks);
            var rz = FloorDiv(bz, spec.RegionBlocks);
            if (!RegionInSearchBox(rx, spec) || !RegionInSearchBox(rz, spec)) continue;

            var centerX = rx * spec.RegionBlocks + spec.RegionBlocks / 2f;
            var centerZ = rz * spec.RegionBlocks + spec.RegionBlocks / 2f;
            if (BiomeClassifier.PreIslandOceanWeight(centerX, centerZ) <= WorldGenerationConfig.OceanShoreWeight) continue;
            if (archipelago
                    ? TerrainIslands.ArchipelagoWeight(centerX, centerZ) < 0.5f
                    : TerrainGenerator.BiomeAt(centerX, centerZ) == Biome.Ocean)
                continue;
            if (!HoldsIslandDryColumns(level, rx, rz)) continue;

            yield return (rx, rz);
        }
    }

    private static bool HoldsIslandDryColumns(int level, int rx, int rz)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var found = 0;
        for (var cx = rx * span - 1; cx <= (rx + 1) * span; cx++)
        for (var cz = rz * span - 1; cz <= (rz + 1) * span; cz++)
        {
            var column = ColumnCache.Get(cx, cz);
            for (var local = 0; local < Chunk.Size * Chunk.Size; local++)
            {
                if (column.WaterLevel[local] > column.Heights[local]) continue;
                var x = cx * Chunk.Size + (local >> Chunk.SizeShift);
                var z = cz * Chunk.Size + (local & Chunk.SizeMask);
                if (BiomeClassifier.PreIslandOceanWeight(x, z) <= WorldGenerationConfig.OceanShoreWeight) continue;
                if (++found >= MinIslandDryColumns) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Level-<paramref name="level"/> regions wholly inside the searched box along a continental coast:
    /// at every 16-block sample of the region and its 1-chunk border the island term is 0, and at some
    /// sample the pre-island ocean weight is above 0. Their shores are counted by the caller.
    /// </summary>
    private static IEnumerable<(int Rx, int Rz)> ContinentalCoastRegions(int level)
    {
        var spec = LodRegion.Levels[level];
        for (var bx = SearchMax - spec.RegionBlocks; bx >= SearchMin; bx -= SearchStride)
        for (var bz = SearchMax - spec.RegionBlocks; bz >= SearchMin; bz -= SearchStride)
        {
            var rx = FloorDiv(bx, spec.RegionBlocks);
            var rz = FloorDiv(bz, spec.RegionBlocks);
            if (!RegionInSearchBox(rx, spec) || !RegionInSearchBox(rz, spec)) continue;
            if (IsContinentalCoast(rx, rz, spec)) yield return (rx, rz);
        }
    }

    private static bool IsContinentalCoast(int rx, int rz, LodLevelSpec spec)
    {
        var anyOcean = false;
        for (var x = rx * spec.RegionBlocks - Chunk.Size; x <= (rx + 1) * spec.RegionBlocks + Chunk.Size; x += 16)
        for (var z = rz * spec.RegionBlocks - Chunk.Size; z <= (rz + 1) * spec.RegionBlocks + Chunk.Size; z += 16)
        {
            var pre = BiomeClassifier.PreIslandOceanWeight(x, z);
            if (TerrainIslands.LandWeight(x, z, pre) > 0f) return false;
            if (pre > 0f) anyOcean = true;
        }

        return anyOcean;
    }

    /// <summary>The highest column over the region's chunk columns plus the 1-chunk border.</summary>
    private static (int X, int Z, int Height) HighestColumn(int level, int rx, int rz)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var best = (X: 0, Z: 0, Height: int.MinValue);
        for (var cx = rx * span - 1; cx <= (rx + 1) * span; cx++)
        for (var cz = rz * span - 1; cz <= (rz + 1) * span; cz++)
        {
            var heights = ColumnCache.GetHeights(cx, cz);
            for (var local = 0; local < heights.Length; local++)
            {
                if (heights[local] <= best.Height) continue;
                best = (cx * Chunk.Size + (local >> Chunk.SizeShift), cz * Chunk.Size + (local & Chunk.SizeMask),
                    heights[local]);
            }
        }

        return best;
    }

    /// <summary>The lowest height of the four columns meeting at vertex corner (x, z), or false when any
    /// of them holds water (the LOD draws a wet column's sheet, not its bed).</summary>
    private static bool DryGroundMin(int x, int z, out int ground)
    {
        ground = int.MaxValue;
        for (var fx = x - 1; fx <= x; fx++)
        for (var fz = z - 1; fz <= z; fz++)
        {
            var column = ColumnCache.Get(fx >> Chunk.SizeShift, fz >> Chunk.SizeShift);
            var local = ((fx & Chunk.SizeMask) << Chunk.SizeShift) | (fz & Chunk.SizeMask);
            var height = column.Heights[local];
            if (column.WaterLevel[local] > height) return false;
            ground = Math.Min(ground, height);
        }

        return true;
    }

    /// <summary>
    /// A level-<paramref name="level"/> region wholly inside the searched box whose center is full
    /// highland on a mountain range and whose terrain spans more than four of the level's cells, so the
    /// shell meets real slopes. The cheap noise checks run first; the column sweep only on survivors.
    /// </summary>
    private static (int Rx, int Rz) FindHighlandRangeRegion(int level)
    {
        var spec = LodRegion.Levels[level];
        for (var bx = SearchMax - spec.RegionBlocks; bx >= SearchMin; bx -= SearchStride)
        for (var bz = SearchMax - spec.RegionBlocks; bz >= SearchMin; bz -= SearchStride)
        {
            var rx = FloorDiv(bx, spec.RegionBlocks);
            var rz = FloorDiv(bz, spec.RegionBlocks);
            if (rx * spec.RegionBlocks < SearchMin || rz * spec.RegionBlocks < SearchMin) continue;

            var centerX = rx * spec.RegionBlocks + spec.RegionBlocks / 2f;
            var centerZ = rz * spec.RegionBlocks + spec.RegionBlocks / 2f;
            if (TerrainRelief.HighlandWeight(centerX, centerZ) < 1f) continue;
            if (TerrainRelief.MountainHeight(centerX, centerZ, 1f) <= 0f) continue;
            if (RegionCeiling(level, rx, rz) - RegionFloor(level, rx, rz) <= 4 * spec.CellSize) continue;

            return (rx, rz);
        }

        Assert.Fail($"no full-highland mountain region with relief past four level-{level} cells in block box " +
                    $"x, z in [{SearchMin}, {SearchMax}]");
        return (0, 0);
    }

    /// <summary>
    /// The level-<paramref name="level"/> region holding the first accepted hashed site (a massif or a
    /// lone peak, read through <paramref name="siteAt"/> over cells of <paramref name="cellBlocks"/>)
    /// whose region lies wholly inside the searched box and whose terrain spans more than
    /// <paramref name="minReliefCells"/> of the level's cells. With <paramref name="summitInsideCells"/>
    /// the region is the one holding the site's summit column (its highest column within a quarter of
    /// its radius), at least that many level cells in from every region edge, so the region's highest
    /// LOD face is the crown's and not a flank whose summit stands in the next region.
    /// </summary>
    private static (int Rx, int Rz) FindSiteRegion(int level, int cellBlocks, Func<int, int, TerrainSite> siteAt,
        int minReliefCells, string what, int summitInsideCells = -1)
    {
        var spec = LodRegion.Levels[level];
        for (var gx = FloorDiv(SearchMax, cellBlocks); gx >= FloorDiv(SearchMin, cellBlocks); gx--)
        for (var gz = FloorDiv(SearchMax, cellBlocks); gz >= FloorDiv(SearchMin, cellBlocks); gz--)
        {
            var site = siteAt(gx, gz);
            if (!site.Exists) continue;

            var (x, z) = summitInsideCells < 0
                ? ((int)MathF.Floor(site.CenterX), (int)MathF.Floor(site.CenterZ))
                : SummitColumn(site);
            var rx = FloorDiv(x, spec.RegionBlocks);
            var rz = FloorDiv(z, spec.RegionBlocks);
            if (!RegionInSearchBox(rx, spec) || !RegionInSearchBox(rz, spec)) continue;
            if (summitInsideCells >= 0 && (!InsideRegion(x, rx, spec, summitInsideCells) ||
                                           !InsideRegion(z, rz, spec, summitInsideCells))) continue;
            if (RegionCeiling(level, rx, rz) - RegionFloor(level, rx, rz) <= minReliefCells * spec.CellSize) continue;

            return (rx, rz);
        }

        Assert.Fail($"no {what} region with relief past {minReliefCells} level-{level} cells in block box " +
                    $"x, z in [{SearchMin}, {SearchMax}]");
        return (0, 0);
    }

    /// <summary>The highest column within a quarter of the site's radius of its center.</summary>
    private static (int X, int Z) SummitColumn(TerrainSite site)
    {
        var reach = (int)(site.Radius / 4f);
        var cx = (int)MathF.Floor(site.CenterX);
        var cz = (int)MathF.Floor(site.CenterZ);
        var best = (X: cx, Z: cz);
        var bestHeight = int.MinValue;
        for (var x = cx - reach; x <= cx + reach; x++)
        for (var z = cz - reach; z <= cz + reach; z++)
        {
            if ((x - cx) * (x - cx) + (z - cz) * (z - cz) > reach * reach) continue;
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var height = column.Heights[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
            if (height <= bestHeight) continue;
            bestHeight = height;
            best = (x, z);
        }

        return best;
    }

    private static bool InsideRegion(int v, int r, LodLevelSpec spec, int cells) =>
        v >= r * spec.RegionBlocks + cells * spec.CellSize && v < (r + 1) * spec.RegionBlocks - cells * spec.CellSize;

    private static bool RegionInSearchBox(int r, LodLevelSpec spec) =>
        r * spec.RegionBlocks >= SearchMin && (r + 1) * spec.RegionBlocks <= SearchMax;

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    /// <summary>The lowest terrain height over the region's chunk columns plus the 1-chunk border the
    /// build prefetches.</summary>
    private static int RegionFloor(int level, int rx, int rz)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var floor = int.MaxValue;
        for (var cx = -1; cx <= span; cx++)
        for (var cz = -1; cz <= span; cz++)
            foreach (var h in ColumnCache.GetHeights(rx * span + cx, rz * span + cz))
                if (h < floor) floor = h;
        return floor;
    }

    private static int RegionCeiling(int level, int rx, int rz)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var ceiling = int.MinValue;
        for (var cx = -1; cx <= span; cx++)
        for (var cz = -1; cz <= span; cz++)
            foreach (var h in ColumnCache.GetHeights(rx * span + cx, rz * span + cz))
                if (h > ceiling) ceiling = h;
        return ceiling;
    }
}
