using System;
using System.IO;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="LodEditState"/> carries one world's far-LOD edits (removal caps, surface overrides,
/// felled-canopy markers) across a restart: the file round-trips, a capture holds the live
/// edits, an import merges without undoing a live edit, and an imported override moves a region
/// build exactly like the live edit it was saved from.
/// </summary>
/// <remarks>
/// The three tables are process-global (reset only by WorldSeed.Apply, which the parallel suite
/// never calls), so each fact that touches them works in its own far level-1 region row
/// (rz 786, 787, 788), clear of every other LOD test's rows.
/// </remarks>
[Collection("LOD column caps")]
public sealed class LodEditStateTests
{
    public LodEditStateTests() => TestContent.EnsureLoaded();

    private const int BucketSize = Chunk.Size * Chunk.Size;
    private const int Level1RegionBlocks = 128;
    private const int ChunksPerLevel1Region = 8;
    private const int CentreChunk = 4;

    private static int HeightAt(int x, int z) =>
        Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    private static int LocalIndex(int x, int z) =>
        ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);

    private static int[] Bucket(int fill) => Enumerable.Repeat(fill, BucketSize).ToArray();

    private static LodEditState Roundtrip(LodEditState state, Func<byte, string?> nameOf, Func<string, byte?> idOf)
    {
        using var stream = new MemoryStream();
        state.Write(stream, nameOf);
        stream.Position = 0;
        return LodEditState.Read(stream, idOf);
    }

    private static void AssertSameBuckets((int CX, int CZ, int[] Values)[] expected, LodEditState actual,
        Func<LodEditState, System.Collections.Generic.IReadOnlyList<(int CX, int CZ, int[] Values)>> section)
    {
        var got = section(actual);
        Assert.Equal(expected.Length, got.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].CX, got[i].CX);
            Assert.Equal(expected[i].CZ, got[i].CZ);
            Assert.Equal(expected[i].Values, got[i].Values);
        }
    }

    [Fact]
    public void WriteReadRoundTripsAllThreeSections()
    {
        // Sentinel-heavy buckets with a few real entries, at negative and positive chunk columns.
        var capA = Bucket(int.MaxValue);
        capA[0] = -40;
        capA[BucketSize - 1] = 12;
        var capB = Bucket(int.MaxValue);
        capB[77] = 300;
        var caps = new[] { (-3, 5, capA), (1000, -2000, capB) };

        var ovrA = Bucket(LodSurface.None);
        ovrA[3] = LodSurface.Pack(-12, 7);
        ovrA[200] = LodSurface.Pack(150, 0);   // the generated tile rule survives as 0
        var ovrB = Bucket(LodSurface.None);
        ovrB[9] = LodSurface.Pack(64, 42);
        ovrB[10] = LodSurface.Pack(65, 7);
        var overrides = new[] { (-8, -9, ovrA), (4, 4, ovrB) };

        // A content-free name mapping, so the palette is exercised without the registry.
        var state = new LodEditState(caps, overrides, []);
        var read = Roundtrip(state, id => "b" + id, name => byte.Parse(name[1..]));

        AssertSameBuckets(caps, read, s => s.Caps);
        AssertSameBuckets(overrides, read, s => s.Overrides);
        Assert.Empty(read.Disturbed);

        var withDisturbed = new LodEditState([], [], [(-5, 7), (int.MaxValue, int.MinValue)]);
        var readDisturbed = Roundtrip(withDisturbed, id => "b" + id, name => byte.Parse(name[1..]));
        Assert.Equal(withDisturbed.Disturbed, readDisturbed.Disturbed);

        // Through the real registry names too, since that is what ToBytes / TryParse use.
        var real = new LodEditState(caps, [(4, 4, ovrB.Select(v => v == LodSurface.None
            ? v
            : LodSurface.Pack(v >> 8, TerrainGenerator.RockBlock)).ToArray())], [(1, 2)]);
        var parsed = LodEditState.TryParse(real.ToBytes());
        Assert.NotNull(parsed);
        AssertSameBuckets([.. real.Overrides], parsed, s => s.Overrides);

        Assert.Null(LodEditState.TryParse(null));
        Assert.Null(LodEditState.TryParse([1, 2, 3, 4, 5, 6, 7, 8]));
        var bytes = real.ToBytes();
        Assert.Null(LodEditState.TryParse(bytes[..(bytes.Length / 2)]));
        Assert.Null(LodEditState.TryParse(bytes[..3]));
    }

    [Fact]
    public void AnUnknownOverrideBlockKeepsItsHeightAndFallsBackToTheGeneratedTile()
    {
        var bucket = Bucket(LodSurface.None);
        bucket[5] = LodSurface.Pack(90, 3);
        bucket[6] = LodSurface.Pack(91, 4);
        var state = new LodEditState([], [(0, 0, bucket)], []);

        // Block 3 has no name when written and block 4's name is unknown when read.
        var read = Roundtrip(state, id => id == 3 ? null : "b" + id, name => name == "b4" ? null : byte.Parse(name[1..]));

        var values = read.Overrides.Single().Values;
        LodSurface.Unpack(values[5], out var top5, out var block5);
        LodSurface.Unpack(values[6], out var top6, out var block6);
        Assert.Equal((90, (byte)0), (top5, block5));
        Assert.Equal((91, (byte)0), (top6, block6));
        Assert.All(values.Where((_, i) => i != 5 && i != 6), v => Assert.Equal(LodSurface.None, v));
    }

    [Fact]
    public void CaptureImportRoundTripsLiveEdits()
    {
        const int Rz = 786;
        var z = Rz * Level1RegionBlocks + 64;
        var capX = 777 * Level1RegionBlocks + 64;
        var ovrX = capX + 40;
        var canopyX = capX + 80;

        var capY = HeightAt(capX, z) - 20;
        Assert.True(LodRegion.LowerColumnBelow(capX, capY, z));
        var ovrTop = HeightAt(ovrX, z) + 11;
        LodSurface.Set(ovrX, z, ovrTop, TerrainGenerator.RockBlock);
        TreeGenerator.PoisonCanopy(canopyX, z, TreeGenerator.SingleEditPoisonRadius);

        var parsed = LodEditState.TryParse(LodEditState.Capture().ToBytes());
        Assert.NotNull(parsed);

        var capBucket = parsed.Caps.Single(b => b.CX == capX >> Chunk.SizeShift && b.CZ == z >> Chunk.SizeShift);
        Assert.Equal(capY - 1, capBucket.Values[LocalIndex(capX, z)]);

        var ovrBucket = parsed.Overrides.Single(b => b.CX == ovrX >> Chunk.SizeShift && b.CZ == z >> Chunk.SizeShift);
        LodSurface.Unpack(ovrBucket.Values[LocalIndex(ovrX, z)], out var top, out var block);
        Assert.Equal(ovrTop, top);
        Assert.Equal(TerrainGenerator.RockBlock, block);

        Assert.Contains((canopyX, z), parsed.Disturbed);
    }

    [Fact]
    public void CombinedVersionMovesWithEveryKindOfEdit()
    {
        const int Rz = 786;
        var z = Rz * Level1RegionBlocks + 96;
        var x = 777 * Level1RegionBlocks + 100;

        var v0 = LodEditState.CombinedVersion;
        Assert.True(LodRegion.LowerColumnBelow(x, HeightAt(x, z) - 25, z));
        var v1 = LodEditState.CombinedVersion;
        LodSurface.Set(x + 1, z, HeightAt(x + 1, z) + 3, TerrainGenerator.RockBlock);
        var v2 = LodEditState.CombinedVersion;
        TreeGenerator.PoisonCanopy(x + 2, z, TreeGenerator.SingleEditPoisonRadius);
        var v3 = LodEditState.CombinedVersion;

        Assert.NotEqual(v0, v1);
        Assert.NotEqual(v1, v2);
        Assert.NotEqual(v2, v3);
    }

    [Fact]
    public void ImportKeepsTheLowerCapAndTheLiveOverride()
    {
        const int Rz = 788;
        var z = Rz * Level1RegionBlocks + 64;
        var baseX = 777 * Level1RegionBlocks;
        int x1 = baseX + 10, x2 = baseX + 30, x3 = baseX + 50, x4 = baseX + 70;
        var cz = z >> Chunk.SizeShift;

        // Column 1: a live cap, then an import of a HIGHER cap for the same column.
        var y = HeightAt(x1, z) - 20;
        Assert.True(LodRegion.LowerColumnBelow(x1, y, z));

        // Column 2: no live cap; the import brings cap c.
        var c = HeightAt(x2, z) - 20;

        // Column 3: a live override, then an import with a different top. Column 4: untouched.
        var t = HeightAt(x3, z) + 6;
        LodSurface.Set(x3, z, t, TerrainGenerator.RockBlock);
        var importedTop4 = HeightAt(x4, z) + 9;
        var importedBlock4 = TerrainGenerator.SurfaceBlock;

        var caps = new System.Collections.Generic.Dictionary<int, int[]>();
        int[] CapBucket(int cx) => caps.TryGetValue(cx, out var b) ? b : caps[cx] = Bucket(int.MaxValue);
        CapBucket(x1 >> Chunk.SizeShift)[LocalIndex(x1, z)] = y + 10;
        CapBucket(x2 >> Chunk.SizeShift)[LocalIndex(x2, z)] = c;

        var ovrs = new System.Collections.Generic.Dictionary<int, int[]>();
        int[] OvrBucket(int cx) => ovrs.TryGetValue(cx, out var b) ? b : ovrs[cx] = Bucket(LodSurface.None);
        OvrBucket(x3 >> Chunk.SizeShift)[LocalIndex(x3, z)] = LodSurface.Pack(t + 20, importedBlock4);
        OvrBucket(x4 >> Chunk.SizeShift)[LocalIndex(x4, z)] = LodSurface.Pack(importedTop4, importedBlock4);

        new LodEditState(
            caps.Select(kv => (kv.Key, cz, kv.Value)).ToArray(),
            ovrs.Select(kv => (kv.Key, cz, kv.Value)).ToArray(),
            []).Import();

        // The live lower cap survived: had the import raised it to y + 10, y would be accepted.
        Assert.False(LodRegion.LowerColumnBelow(x1, y, z));

        // The imported cap landed and stays monotone.
        Assert.False(LodRegion.LowerColumnBelow(x2, c + 1, z));
        Assert.True(LodRegion.LowerColumnBelow(x2, c - 1, z));

        // The live override won over the saved one.
        Assert.True(LodSurface.TryGet(x3, z, out var top3, out var block3));
        Assert.Equal((t, TerrainGenerator.RockBlock), (top3, block3));

        // An untouched column takes the saved override.
        Assert.True(LodSurface.TryGet(x4, z, out var top4, out var block4));
        Assert.Equal((importedTop4, importedBlock4), (top4, block4));
    }

    [Fact]
    public void ImportedCanopyMarkersHideTheCanopy()
    {
        // Search a far forest for a canopy column; the marker must hide it once imported.
        var z0 = 788 * Level1RegionBlocks + 110;
        for (var x = 777 * Level1RegionBlocks; x < 777 * Level1RegionBlocks + 40000; x += 7)
        {
            if (!TreeGenerator.TryGetCanopy(x, z0, out _, out _)) continue;

            new LodEditState([], [], [(x, z0)]).Import();
            Assert.False(TreeGenerator.TryGetCanopy(x, z0, out _, out _));
            return;
        }

        Assert.Fail("no canopy column found along the search row");
    }

    [Fact]
    public void ImportedOverrideChangesARegionBuildLikeALiveEdit()
    {
        const int Rz = 787;
        var rx = LodSurfaceTests.FindQuietFrozenRx(Rz);
        var cx = rx * ChunksPerLevel1Region + CentreChunk;
        var cz = Rz * ChunksPerLevel1Region + CentreChunk;
        var y = Chunk.GetColumnHeights(cx, cz).Max() + 8;

        var before = LodRegion.BuildMeshData(1, rx, Rz);

        // A full 16x16 plateau, saved and parsed back through the file format before the import.
        var plateau = Bucket(LodSurface.Pack(y, TerrainGenerator.RockBlock));
        var parsed = LodEditState.TryParse(new LodEditState([], [(cx, cz, plateau)], []).ToBytes());
        Assert.NotNull(parsed);
        parsed.Import();

        var after = LodRegion.BuildMeshData(1, rx, Rz);

        var px = (cx << Chunk.SizeShift) + 8.5f;
        var pz = (cz << Chunk.SizeShift) + 8.5f;
        var b = LodSurfaceTests.TopFaceYAt(before, px, pz);
        var a = LodSurfaceTests.TopFaceYAt(after, px, pz);
        Assert.True(a > b, $"the imported plateau left the level-1 surface at {b}");
        Assert.True(a <= y + 1, $"level 1 at {a} stands above the plateau top {y + 1}");
        Assert.True(after.ColumnCyMax[CentreChunk * ChunksPerLevel1Region + CentreChunk] >= y >> Chunk.SizeShift);
    }
}
