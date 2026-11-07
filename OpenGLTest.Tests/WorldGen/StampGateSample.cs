using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.WorldGen.Sky;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The fixed, deterministic chunk set the PERF_TODO G6 stamp-gate proof generates (the gated fill
/// against the ungated one, and every stamp's writes against its band). It covers:
/// <list type="bullet">
/// <item>category anchors drawn with SplitMix64 over chunk columns in [-1250, 1250) on both axes
/// (the cost probe's range), classified at the column center with the cost probe's cheap point
/// predicates: the forest, steppe, savanna, taiga, frozen and desert biomes, the ocean, lakes and
/// rivers, highlands, massifs, great lakes and island land (an island column is one whose
/// <see cref="TerrainIslands.LandWeight(float, float, float)"/> over its pre-island ocean weight is
/// past one half). Each anchor expands to its 2x2 block of chunk columns, which share tree cells
/// and so keep the cold tree-batch cost down;</item>
/// <item>targeted feature sites, found by a bounded outward ring scan of each feature's own site
/// cells (its <c>TryGet*</c> helper) starting at the cell of a fitting anchor: fallen logs,
/// boulders, cairns (summits first, any site when the scan finds too few), ruined pillars, desert
/// wells, meteor craters, geodes (with the chunks the geode itself spans, however deep), ice spikes
/// on the frozen plains and oases. Every chunk column a site's footprint touches is added;</item>
/// <item>for every column, every chunk from two below its lowest ground to one above its tallest
/// ground plus the tree reach, plus the chunk holding its highest water level and the one above
/// it. The sky chunks in that range simply take the fill's early-out.</item>
/// </list>
/// The default set is sized for the default test suite; setting the environment variable
/// <see cref="ExhaustiveVariable"/> to <c>1</c> scales every count up (thousands of chunks) for
/// the exhaustive proof run. The set must stay a pure function of the world seed and the shipped
/// content: it reads only worldgen and the generators' own site rules, never a clock or a runtime
/// random source, so two runs over the same build produce the same positions in the same order.
/// </summary>
internal static class StampGateSample
{
    /// <summary>The environment variable that switches the G6 proof to its exhaustive sample.</summary>
    internal const string ExhaustiveVariable = "VOXEL_STAMP_GATES_EXHAUSTIVE";

    /// <summary>Whether <see cref="ExhaustiveVariable"/> asks for the exhaustive sample.</summary>
    internal static bool Exhaustive => Environment.GetEnvironmentVariable(ExhaustiveVariable) == "1";

    private const ulong DrawSeed = 0x6E1D_C0DE_0006UL;
    private const int ColumnRange = 1250;

    // The site scans walk at most this many rings of site cells out from their start cell
    private const int MaxSiteRing = 12;

    // The ice-spike scan walks at most this many rings of chunk columns around a frozen anchor
    private const int MaxSpikeRing = 12;

    private const int Forest = 0;
    private const int Steppe = 1;
    private const int Savanna = 2;
    private const int Taiga = 3;
    private const int Frozen = 4;
    private const int Desert = 5;
    private const int Ocean = 6;
    private const int LakeRiver = 7;
    private const int Highland = 8;
    private const int Massif = 9;
    private const int GreatLake = 10;
    private const int Island = 11;
    private const int CategoryCount = 12;

    /// <summary>
    /// The sample's chunk positions (in chunk units), deduplicated and in a fixed insertion order:
    /// the default set, or the exhaustive one.
    /// </summary>
    internal static List<Vector3> Chunks(bool exhaustive)
    {
        var anchorsPerCategory = exhaustive ? 24 : 2;
        var maxDraws = exhaustive ? 40000 : 6000;
        var sitesPerFeature = exhaustive ? 12 : 2;

        var anchors = DrawAnchors(anchorsPerCategory, maxDraws);

        var columns = new List<(int Cx, int Cz)>();
        var seenColumns = new HashSet<(int, int)>();
        var extraChunks = new List<Vector3>();

        void AddColumn(int cx, int cz)
        {
            if (seenColumns.Add((cx, cz)))
                columns.Add((cx, cz));
        }

        // Every chunk column a world-space box of columns touches
        void AddFootprint(int minX, int minZ, int maxX, int maxZ)
        {
            for (var cx = minX >> Chunk.SizeShift; cx <= maxX >> Chunk.SizeShift; cx++)
            for (var cz = minZ >> Chunk.SizeShift; cz <= maxZ >> Chunk.SizeShift; cz++)
                AddColumn(cx, cz);
        }

        foreach (var list in anchors)
        foreach (var (cx, cz) in list)
        {
            AddColumn(cx, cz);
            AddColumn(cx + 1, cz);
            AddColumn(cx, cz + 1);
            AddColumn(cx + 1, cz + 1);
        }

        AddSites(anchors, sitesPerFeature, AddFootprint, extraChunks);

        var chunks = new List<Vector3>();
        var seenChunks = new HashSet<Vector3>();

        void AddChunk(Vector3 pos)
        {
            if (seenChunks.Add(pos))
                chunks.Add(pos);
        }

        foreach (var (cx, cz) in columns)
        {
            var column = ColumnCache.Get(cx, cz);
            var low = (column.MinHeight >> Chunk.SizeShift) - 2;
            var high = ((column.MaxHeight + TreeGenerator.ReachUp) >> Chunk.SizeShift) + 1;
            for (var cy = low; cy <= high; cy++)
                AddChunk(new Vector3(cx, cy, cz));

            if (column.MaxWaterLevel == TerrainGenerator.NoWater) continue;
            var water = column.MaxWaterLevel >> Chunk.SizeShift;
            AddChunk(new Vector3(cx, water, cz));
            AddChunk(new Vector3(cx, water + 1, cz));
        }

        foreach (var pos in extraChunks)
            AddChunk(pos);

        // The barrier band lies far below every column's ground: one anchor column's chunk stack
        // through it, one chunk past each edge, so its gate both admits and skips a filled chunk,
        // and on down through hell's first two layers, whose wholly-hell chunks take the fill's
        // hell path (gated) against the whole pipeline (ungated)
        if (columns.Count > 0)
        {
            var (bcx, bcz) = columns[0];
            var hellBottom = WorldGenerationConfig.HellTopY - 2 * WorldGenerationConfig.HellLayerPeriodBlocks;
            for (var cy = hellBottom >> Chunk.SizeShift;
                 cy <= (WorldGenerationConfig.BarrierTopY >> Chunk.SizeShift) + 1; cy++)
                AddChunk(new Vector3(bcx, cy, bcz));
        }

        // The cloud slab lies far above every column's ground: the chunk stacks through it of the
        // anchor column and of the nearest chunk column holding cloud, one chunk past each edge, so
        // its gate both admits and skips and the sky chunk's short path meets real cloud
        if (columns.Count > 0)
        {
            var (ccx, ccz) = columns[0];
            var cloudy = NearestCloudColumn(ccx, ccz);
            for (var cy = (WorldGenerationConfig.CloudSlabBottomY >> Chunk.SizeShift) - 1;
                 cy <= (WorldGenerationConfig.CloudSlabTopY >> Chunk.SizeShift) + 1; cy++)
            {
                AddChunk(new Vector3(ccx, cy, ccz));
                AddChunk(new Vector3(cloudy.Cx, cy, cloudy.Cz));
            }
        }

        return chunks;
    }

    /// <summary>The chunk column nearest (cx, cz), by rings, with a cloud column at its center.</summary>
    private static (int Cx, int Cz) NearestCloudColumn(int cx, int cz)
    {
        for (var ring = 0; ring <= MaxSiteRing * 4; ring++)
        for (var dx = -ring; dx <= ring; dx++)
        for (var dz = -ring; dz <= ring; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
            if (CloudPatchGenerator.TryGetColumn(((cx + dx) << Chunk.SizeShift) + 8, ((cz + dz) << Chunk.SizeShift) + 8, out _, out _))
                return (cx + dx, cz + dz);
        }

        return (cx, cz);
    }

    /// <summary>
    /// Draws chunk columns and sorts each into every category it matches until all are full or the
    /// draw cap is reached; the costly predicates run only while their category still has room.
    /// </summary>
    private static List<(int Cx, int Cz)>[] DrawAnchors(int perCategory, int maxDraws)
    {
        var lists = new List<(int Cx, int Cz)>[CategoryCount];
        for (var c = 0; c < lists.Length; c++)
            lists[c] = [];

        var state = DrawSeed;
        for (var draw = 0; draw < maxDraws; draw++)
        {
            if (AllFull(lists, perCategory))
                break;

            var cx = (int)(NextRandom(ref state) % (2 * ColumnRange)) - ColumnRange;
            var cz = (int)(NextRandom(ref state) % (2 * ColumnRange)) - ColumnRange;
            float x = cx * Chunk.Size + 8;
            float z = cz * Chunk.Size + 8;

            var biome = TerrainGenerator.BiomeAt(x, z);
            var ocean = biome == Biome.Ocean;
            var category = biome switch
            {
                Biome.Forest => Forest,
                Biome.Steppe => Steppe,
                Biome.Savanna => Savanna,
                Biome.Taiga => Taiga,
                Biome.Frozen => Frozen,
                Biome.Desert => Desert,
                _ => Ocean,
            };
            TryAdd(lists[category], perCategory, cx, cz);

            if (!ocean && lists[LakeRiver].Count < perCategory && TerrainGenerator.SurfaceWaterWeight(x, z) > 0f)
                TryAdd(lists[LakeRiver], perCategory, cx, cz);
            if (lists[Highland].Count < perCategory && TerrainRelief.HighlandWeight(x, z) > 0.5f)
                TryAdd(lists[Highland], perCategory, cx, cz);
            if (lists[Massif].Count < perCategory && TerrainPeaks.MassifHeight(x, z) > 0f)
                TryAdd(lists[Massif], perCategory, cx, cz);
            if (lists[GreatLake].Count < perCategory
                && GreatLakes.Sample(x, z, out _, out _, out _, out _, out _, out _, out _) > 0f)
                TryAdd(lists[GreatLake], perCategory, cx, cz);
            if (lists[Island].Count < perCategory
                && TerrainIslands.LandWeight(x, z, BiomeClassifier.PreIslandOceanWeight(x, z)) > 0.5f)
                TryAdd(lists[Island], perCategory, cx, cz);
        }

        return lists;
    }

    private static bool AllFull(List<(int Cx, int Cz)>[] lists, int perCategory)
    {
        foreach (var list in lists)
            if (list.Count < perCategory)
                return false;
        return true;
    }

    private static void TryAdd(List<(int Cx, int Cz)> list, int target, int cx, int cz)
    {
        if (list.Count < target)
            list.Add((cx, cz));
    }

    /// <summary>
    /// The targeted feature sites: each feature's site cells scanned outward from a fitting
    /// anchor's cell, every accepted site's footprint added through <paramref name="addFootprint"/>
    /// (min x, min z, max x, max z in world columns) and a geode's own chunks to
    /// <paramref name="extraChunks"/>.
    /// </summary>
    private static void AddSites(List<(int Cx, int Cz)>[] anchors, int want,
        Action<int, int, int, int> addFootprint, List<Vector3> extraChunks)
    {
        // Fallen logs lie in the forest, taiga and savanna
        ScanCells(StartBlock(anchors, Forest), WorldFeatureConfig.FallenLogCellShift, want, (cellX, cellZ) =>
        {
            if (!FallenLogGenerator.TryGetPlacedLog(cellX, cellZ, out var x, out var z, out var length,
                    out var alongX, out _, out _))
                return false;
            addFootprint(x, z, alongX ? x + length - 1 : x, alongX ? z : z + length - 1);
            return true;
        });

        // Boulders sit on the grass biomes' floor
        ScanCells(StartBlock(anchors, Steppe), WorldFeatureConfig.BoulderCellShift, want, (cellX, cellZ) =>
        {
            if (!BoulderGenerator.TryGetBoulder(cellX, cellZ, out var x, out var z, out _, out _))
                return false;
            if (TerrainGenerator.BiomeAt(x, z) is not (Biome.Forest or Biome.Taiga or Biome.Steppe or Biome.Savanna))
                return false;
            addFootprint(x - 1, z - 1, x + 1, z + 1);
            return true;
        });

        // Cairns stand on summits: take summit sites first, then any site if the scan found too few
        var cairnStart = StartBlock(anchors, anchors[Massif].Count > 0 ? Massif : Highland);
        var summits = ScanCells(cairnStart, WorldFeatureConfig.CairnCellShift, want, (cellX, cellZ) =>
        {
            if (!CairnGenerator.TryGetCairn(cellX, cellZ, out var x, out var z) || !CairnGenerator.IsSummit(x, z))
                return false;
            addFootprint(x, z, x, z);
            return true;
        });
        ScanCells(cairnStart, WorldFeatureConfig.CairnCellShift, want - summits, (cellX, cellZ) =>
        {
            if (!CairnGenerator.TryGetCairn(cellX, cellZ, out var x, out var z))
                return false;
            addFootprint(x, z, x, z);
            return true;
        });

        ScanCells(StartBlock(anchors, Savanna), WorldFeatureConfig.PillarCellShift, want, (cellX, cellZ) =>
        {
            if (!RuinedPillarGenerator.TryGetPillar(cellX, cellZ, out var x, out var z, out _, out _))
                return false;
            addFootprint(x, z, x, z);
            return true;
        });

        ScanCells(StartBlock(anchors, Desert), WorldFeatureConfig.WellCellShift, want, (cellX, cellZ) =>
        {
            if (!DesertWellGenerator.TryGetWell(cellX, cellZ, out var x, out var z)
                || TerrainGenerator.BiomeAt(x, z) != Biome.Desert)
                return false;
            addFootprint(x - 2, z - 2, x + 2, z + 2);
            return true;
        });

        ScanCells(StartBlock(anchors, Forest), WorldFeatureConfig.MeteorCellShift, want, (cellX, cellZ) =>
        {
            if (!MeteorGenerator.TryGetCrater(cellX, cellZ, out var x, out var z, out var radius))
                return false;
            addFootprint(x - radius, z - radius, x + radius, z + radius);
            return true;
        });

        ScanCells(StartBlock(anchors, Taiga), WorldFeatureConfig.GeodeCellShift, want, (cellX, cellZ) =>
        {
            if (!GeodeGenerator.TryGetGeode(cellX, cellZ, out var x, out var y, out var z, out var radius))
                return false;
            addFootprint(x - radius, z - radius, x + radius, z + radius);
            var cx = x >> Chunk.SizeShift;
            var cz = z >> Chunk.SizeShift;
            extraChunks.Add(new Vector3(cx, (y - radius) >> Chunk.SizeShift, cz));
            extraChunks.Add(new Vector3(cx, y >> Chunk.SizeShift, cz));
            extraChunks.Add(new Vector3(cx, (y + radius) >> Chunk.SizeShift, cz));
            return true;
        });

        ScanCells(StartBlock(anchors, Desert), WorldFeatureConfig.OasisCellShift, want, (cellX, cellZ) =>
        {
            if (!OasisGenerator.TryGetCenter(cellX, cellZ, out var x, out var z, out _))
                return false;
            var r = WorldFeatureConfig.OasisRadius;
            addFootprint(x - r, z - r, x + r, z + r);
            return true;
        });

        var spikeStart = StartBlock(anchors, Frozen);
        ScanSpikes((spikeStart.X >> Chunk.SizeShift, spikeStart.Z >> Chunk.SizeShift), want, addFootprint);
    }

    /// <summary>The world block at the center of a category's first anchor, or a fixed point when it has none.</summary>
    private static (int X, int Z) StartBlock(List<(int Cx, int Cz)>[] anchors, int category) =>
        anchors[category].Count > 0
            ? ((anchors[category][0].Cx << Chunk.SizeShift) + 8, (anchors[category][0].Cz << Chunk.SizeShift) + 8)
            : (3000, -2000);

    /// <summary>
    /// Walks the site cells of a <paramref name="cellShift"/> grid ring by ring out from the cell
    /// holding <paramref name="start"/>, at most <see cref="MaxSiteRing"/> rings, until
    /// <paramref name="want"/> cells were accepted; returns how many were.
    /// </summary>
    private static int ScanCells((int X, int Z) start, int cellShift, int want, Func<int, int, bool> trySite)
    {
        var startX = start.X >> cellShift;
        var startZ = start.Z >> cellShift;
        var found = 0;
        for (var ring = 0; ring <= MaxSiteRing && found < want; ring++)
        for (var dx = -ring; dx <= ring && found < want; dx++)
        for (var dz = -ring; dz <= ring && found < want; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
            if (trySite(startX + dx, startZ + dz)) found++;
        }

        return found;
    }

    /// <summary>
    /// Ice spikes are decided per world column: walks the chunk columns ring by ring out from a
    /// start chunk column (a frozen anchor's), testing every world column, until
    /// <paramref name="want"/> spikes were found.
    /// </summary>
    private static void ScanSpikes((int Cx, int Cz) anchor, int want, Action<int, int, int, int> addFootprint)
    {
        var found = 0;
        for (var ring = 0; ring <= MaxSpikeRing && found < want; ring++)
        for (var dcx = -ring; dcx <= ring && found < want; dcx++)
        for (var dcz = -ring; dcz <= ring && found < want; dcz++)
        {
            if (Math.Max(Math.Abs(dcx), Math.Abs(dcz)) != ring) continue;
            var x0 = (anchor.Cx + dcx) << Chunk.SizeShift;
            var z0 = (anchor.Cz + dcz) << Chunk.SizeShift;
            for (var x = x0; x < x0 + Chunk.Size && found < want; x++)
            for (var z = z0; z < z0 + Chunk.Size && found < want; z++)
            {
                if (!IceSpikeGenerator.TryGetSpike(x, z, out _, out _)) continue;
                addFootprint(x, z, x, z);
                found++;
            }
        }
    }

    private static ulong NextRandom(ref ulong state) => SplitMix64.Mix(state += 0x9E3779B97F4A7C15UL);
}
