using System;
using System.Collections.Generic;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Lod;

/// <summary>Frozen natural shore coverage at the default Legacy seed.</summary>
[Collection("Worldgen seed")]
public sealed class FixedShoreCoverageTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // Historical seed-1337 Legacy shore cohort, 7 islands and 6 continental coasts per level.
    // Coordinates are frozen; pit counts and shares from the original run are diagnostics, not a golden.
    private static readonly (int Level, int Rx, int Rz, string Kind)[] FixedShoreRegions =
    [
        (0, -38, -53, "island"),
        (0, -38, -128, "island"),
        (0, -38, -403, "island"),
        (0, -43, -203, "island"),
        (0, -53, -218, "island"),
        (0, -58, -443, "island"),
        (0, -43, -78, "island"),
        (0, -33, -183, "coast"),
        (0, -33, -358, "coast"),
        (0, -33, -448, "coast"),
        (0, -38, -43, "coast"),
        (0, -38, -73, "coast"),
        (0, -38, -103, "coast"),

        (1, -17, -27, "island"),
        (1, -17, -170, "island"),
        (1, -20, -40, "island"),
        (1, -20, -105, "island"),
        (1, -20, -230, "island"),
        (1, -22, -40, "island"),
        (1, -20, -97, "island"),
        (1, -17, -100, "coast"),
        (1, -17, -147, "coast"),
        (1, -17, -187, "coast"),
        (1, -17, -222, "coast"),
        (1, -20, -22, "coast"),
        (1, -20, -60, "coast"),

        (2, -9, -14, "island"),
        (2, -9, -73, "island"),
        (2, -9, -86, "island"),
        (2, -11, -49, "island"),
        (2, -11, -52, "island"),
        (2, -11, -94, "island"),
        (2, -11, -51, "island"),
        (2, -9, -19, "coast"),
        (2, -9, -27, "coast"),
        (2, -9, -43, "coast"),
        (2, -9, -44, "coast"),
        (2, -9, -46, "coast"),
        (2, -9, -51, "coast"),
    ];

    [Fact]
    public void FixedIslandAndCoastShoreCellsHaveGroundOrWaterCoverage()
    {
        TestContent.EnsureLoaded();
        var previousSeed = WorldSeed.Value;
        var previousProfile = WorldSeed.Profile;
        var previousDisturbed = TreeCanopy.ExportCanopyDisturbed();
        var previousAdded = TreeCanopy.ExportCanopyAdded();
        var applied = false;
        try
        {
            WorldSeed.Apply(WorldSeed.Default, WorldGenProfile.Legacy);
            applied = true;
            var totals = new int[LodRegion.Levels.Length, 2];
            foreach (var (level, rx, rz, kind) in FixedShoreRegions)
            {
                var spec = LodRegion.Levels[level];
                var mesh = LodRegion.BuildMeshData(level, rx, rz);
                var shore = ShoreWindowCells(level, rx, rz, out var shoreCells, out var shorelineColumns);
                var ground = new bool[spec.Cells * spec.Cells];
                var water = new bool[ground.Length];
                var top = new float[ground.Length];
                Array.Fill(top, float.MinValue);
                for (var q = 0; q + 3 < mesh.Vertices.Count; q += 4)
                {
                    var corner = mesh.Vertices[q];
                    if ((corner.Normal & 7) != 4 || corner.PackedColor >> 24 != 255) continue;
                    QuadBounds(mesh.Vertices, q, spec.CellSize, spec.Cells, out var i0, out var i1,
                        out var j0, out var j1, out var y);
                    for (var i = i0; i <= i1; i++)
                    for (var j = j0; j <= j1; j++)
                    {
                        var c = i * spec.Cells + j;
                        ground[c] = true;
                        top[c] = MathF.Max(top[c], y);
                    }
                }
                for (var q = 0; q + 3 < mesh.WaterVertices.Count; q += 4)
                {
                    QuadBounds(mesh.WaterVertices, q, spec.CellSize, spec.Cells, out var i0, out var i1,
                        out var j0, out var j1, out _);
                    for (var i = i0; i <= i1; i++)
                    for (var j = j0; j <= j1; j++)
                        water[i * spec.Cells + j] = true;
                }

                var missing = 0;
                var dryPits = 0;
                for (var c = 0; c < shore.Length; c++)
                {
                    if (!shore[c]) continue;
                    if (!ground[c] && !water[c]) missing++;
                    if (!water[c] && top[c] < WorldGenerationConfig.SeaLevel - spec.CellSize) dryPits++;
                }
                Assert.True(shoreCells > 0 && shorelineColumns > 0,
                    $"empty {kind} shore at level {level} ({rx}, {rz})");
                Assert.True(missing == 0,
                    $"{kind} shore at level {level} ({rx}, {rz}) leaves {missing} cells without ground or water");
                totals[level, kind == "island" ? 0 : 1] += shoreCells;
                _output.WriteLine($"level {level} {kind} ({rx}, {rz}): {dryPits}/{shoreCells} dry pits " +
                    $"(share {dryPits / (float)shoreCells:F3}), " +
                    $"ring {dryPits * spec.CellSize * spec.CellSize / (float)shorelineColumns:F2}, " +
                    $"missing ground/water {missing}");
            }
            for (var level = 0; level < LodRegion.Levels.Length; level++)
            {
                Assert.True(totals[level, 0] > 0, $"level {level} has no island shore coverage");
                Assert.True(totals[level, 1] > 0, $"level {level} has no coast shore coverage");
            }
        }
        finally
        {
            if (applied)
            {
                WorldSeed.Apply(previousSeed, previousProfile);
                TreeCanopy.ImportCanopyDisturbed(previousDisturbed);
                TreeCanopy.ImportCanopyAdded(previousAdded);
            }
        }
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
    /// Which of the region's level cells are shore-window cells, how
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
}
