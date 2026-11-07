using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// A MEASUREMENT HARNESS for PERF_TODO G1, not a regression guard. It prints the cost of
/// <see cref="TerrainGenerator.Compute"/> per chunk column over fixed, SplitMix64-drawn column
/// categories (land, ocean, lake/river, highland, massif, great lake), and the per-stage cost of
/// <c>ChunkTerrainFill.GenerateBlockData</c> through <see cref="WorldgenStageTimer"/> on fixed
/// surface, above-surface, sky, deep-stone, cave-heavy, sea-floor and hell chunks, each timed cold and
/// warm, so a worldgen change can report its before/after cost on the same workload.
///
/// Nothing here asserts a TIMING: this is a shared dev machine with no CI, so a timing assertion
/// would be a flake generator. The numbers go through <see cref="ITestOutputHelper"/> and are read
/// by a human. The only assertions are anti-vacuity: the workload must not be empty, so a
/// silently-skipped pass cannot be mistaken for a fast one. The probe is single-threaded, so it
/// sees no worker contention. A cold sample (every worldgen cache cleared by
/// <see cref="WorldSeed.Apply"/>, then a GC, both untimed) overstates the in-game cost, and a warm
/// sample (the same work repeated, CPU caches hot) understates it: quote them as a bracket and as
/// before/after pairs, never as absolute in-game rates.
///
/// Run it with <c>--filter FullyQualifiedName~WorldgenCostProbe --logger "console;verbosity=detailed"</c>.
/// It runs in the default suite, so its workload constants are sized to keep both facts near
/// 10 s together (the cold chunk samples dominate): for a steadier figure, run it several times
/// and take the median of the runs rather than raising the constants.
/// </summary>
[Collection("Worldgen seed")]
public sealed class WorldgenCostProbe
{
    private readonly ITestOutputHelper _output;

    public WorldgenCostProbe(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    private const ulong ColumnSeed = 0x6E1D_C0DE_0001UL;
    private const int AllTarget = 64;
    private const int CategoryTarget = 32;
    private const int MaxColumnDraws = 4000;
    private const int Reps = 2;

    private static readonly string[] CategoryNames =
        ["all", "land", "ocean", "lakeRiver", "highland", "massif", "greatLake"];

    private const ulong ChunkSeed = 0x6E1D_C0DE_0002UL;
    private const int PositionsPerClass = 6;
    private const int SkyPositions = 4;
    private const int ChunkReps = 1;
    private const int WarmPerCold = 2;
    private const int UndergroundCandidates = 24;
    private const int MaxChunkDraws = 3000;

    // Chunk classes, in measurement order (index order of ChunkClassNames)
    private const int Surface = 0;
    private const int Above = 1;
    private const int Sky = 2;
    private const int DeepStone = 3;
    private const int CaveHeavy = 4;
    private const int SeaFloor = 5;
    private const int Hell = 6;

    // Hell chunks are drawn from this many chunk layers under the one straddling hell's top
    private const int HellChunkLayers = 36;

    private static readonly string[] ChunkClassNames =
        ["surface", "above", "sky", "deepStone", "caveHeavy", "seaFloor", "hell"];

    [Fact]
    public void ComputeCostPerColumnCategory()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var lists = ClassifyColumns();

            // Warm-up for the JIT, untimed
            foreach (var (cx, cz) in lists[0])
                TerrainGenerator.Compute(cx, cz);

            var cold = new double[lists.Length][];
            var warm = new double[lists.Length][];
            for (var c = 0; c < lists.Length; c++)
            {
                cold[c] = new double[lists[c].Count * Reps];
                warm[c] = new double[lists[c].Count * Reps];
            }

            long checksum = 0;
            for (var rep = 0; rep < Reps; rep++)
            for (var c = 0; c < lists.Length; c++)
            {
                var list = lists[c];
                if (list.Count == 0)
                    continue;

                ResetCold();
                checksum += TimePass(list, cold[c], rep * list.Count);
                checksum += TimePass(list, warm[c], rep * list.Count);
            }

            _output.WriteLine($"Compute cost per chunk column, {Reps} reps, cold then warm per category (checksum {checksum})");
            for (var c = 0; c < lists.Length; c++)
            {
                var n = lists[c].Count;
                if (n == 0)
                {
                    _output.WriteLine($"  {CategoryNames[c],-10} none found in {MaxColumnDraws} draws");
                    continue;
                }

                var (cMed, cP90, cMean) = Stats(cold[c], n * Reps);
                var (wMed, wP90, wMean) = Stats(warm[c], n * Reps);
                _output.WriteLine(
                    $"  {CategoryNames[c],-10} {n,3} columns  cold med {cMed:F3} p90 {cP90:F3} mean {cMean:F3}  " +
                    $"warm med {wMed:F3} p90 {wP90:F3} mean {wMean:F3} ms/column  cold/warm {cMean / wMean:F2}x");
            }

            _output.WriteLine("  reference: PERF_TODO baseline 2026-10-31, Compute 0.2-0.4 ms/column");

            Assert.NotEmpty(lists[0]);
            Assert.True(cold[0].Length > 0);
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    /// <summary>
    /// Draws chunk columns and sorts each into every category it matches (index order of
    /// <see cref="CategoryNames"/>) until all are full or the draw cap is reached. Untimed.
    /// </summary>
    private static List<(int Cx, int Cz)>[] ClassifyColumns()
    {
        var lists = new List<(int Cx, int Cz)>[CategoryNames.Length];
        for (var c = 0; c < lists.Length; c++)
            lists[c] = [];

        var state = ColumnSeed;
        for (var draw = 0; draw < MaxColumnDraws; draw++)
        {
            if (AllFull(lists))
                break;

            var (cx, cz) = NextChunkColumn(ref state);
            float x = cx * Chunk.Size + 8;
            float z = cz * Chunk.Size + 8;

            var ocean = TerrainGenerator.BiomeAt(x, z) == Biome.Ocean;
            var water = !ocean && TerrainGenerator.SurfaceWaterWeight(x, z) > 0f;
            TryAdd(lists[0], AllTarget, cx, cz, true);
            TryAdd(lists[1], CategoryTarget, cx, cz, !ocean && !water);
            TryAdd(lists[2], CategoryTarget, cx, cz, ocean);
            TryAdd(lists[3], CategoryTarget, cx, cz, water);
            TryAdd(lists[4], CategoryTarget, cx, cz, TerrainRelief.HighlandWeight(x, z) > 0.5f);
            TryAdd(lists[5], CategoryTarget, cx, cz, TerrainPeaks.MassifHeight(x, z) > 0f);
            TryAdd(lists[6], CategoryTarget, cx, cz,
                GreatLakes.Sample(x, z, out _, out _, out _, out _, out _, out _, out _) > 0f);
        }

        return lists;
    }

    private static bool AllFull(List<(int Cx, int Cz)>[] lists)
    {
        if (lists[0].Count < AllTarget)
            return false;
        for (var c = 1; c < lists.Length; c++)
            if (lists[c].Count < CategoryTarget)
                return false;
        return true;
    }

    private static void TryAdd(List<(int Cx, int Cz)> list, int target, int cx, int cz, bool matches)
    {
        if (matches && list.Count < target)
            list.Add((cx, cz));
    }

    /// <summary>Times each column's <see cref="TerrainGenerator.Compute"/> on its own; returns the checksum.</summary>
    private static long TimePass(List<(int Cx, int Cz)> list, double[] into, int offset)
    {
        long checksum = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var (cx, cz) = list[i];
            var t0 = Stopwatch.GetTimestamp();
            var result = TerrainGenerator.Compute(cx, cz);
            var t1 = Stopwatch.GetTimestamp();
            into[offset + i] = TicksToMs(t1 - t0);
            checksum += result.MaxHeight;
        }

        return checksum;
    }

    [Fact]
    public void ChunkFillCostPerStage()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var classes = SelectChunks();
            var timer = new WorldgenStageTimer();

            // Warm-up for the JIT: each class's first chunk once cold and once warm, untimed
            var scratch = new ChunkSamples(2);
            foreach (var positions in classes)
            {
                if (positions.Count == 0)
                    continue;
                scratch.Count = 0;
                ResetCold();
                TimeChunk(positions[0], timer, scratch);
                TimeChunk(positions[0], timer, scratch);
            }

            var cold = new ChunkSamples[classes.Length];
            var warm = new ChunkSamples[classes.Length];
            for (var c = 0; c < classes.Length; c++)
            {
                cold[c] = new ChunkSamples(classes[c].Count * ChunkReps);
                warm[c] = new ChunkSamples(classes[c].Count * ChunkReps * WarmPerCold);
            }

            var anyNonSkySolid = false;
            for (var rep = 0; rep < ChunkReps; rep++)
            for (var c = 0; c < classes.Length; c++)
            foreach (var pos in classes[c])
            {
                ResetCold();
                var solid = TimeChunk(pos, timer, cold[c]);
                if (c != Sky)
                    anyNonSkySolid |= solid > 0;

                for (var w = 0; w < WarmPerCold; w++)
                    TimeChunk(pos, timer, warm[c]);
            }

            _output.WriteLine(
                $"GenerateBlockData cost per stage, {ChunkReps} reps, each 1 cold (caches cleared + GC) then {WarmPerCold} warm samples per chunk");
            for (var c = 0; c < classes.Length; c++)
            {
                _output.WriteLine($"[{ChunkClassNames[c]}] {classes[c].Count} chunks");
                Report("cold", cold[c]);
                Report("warm", warm[c]);
            }

            _output.WriteLine("[all classes] probe class mix, NOT the in-game distribution: not comparable to the in-game wgen median");
            Report("cold", ChunkSamples.Concat(cold));
            Report("warm", ChunkSamples.Concat(warm));

            _output.WriteLine(
                "reference: PERF_TODO 2026-10-31 in-game wgen median 1.31 ms/chunk, cold 29-34 ms; " +
                "MeshBuildCostProbe 5475 us/build (synthetic soup, not real terrain: no real mesh-vs-worldgen ratio measured)");

            var total = 0;
            for (var c = 0; c < classes.Length; c++)
                total += cold[c].Count + warm[c].Count;
            Assert.True(total > 0, "no chunk sample was timed");
            Assert.True(anyNonSkySolid, "no sampled non-sky chunk generated a solid block");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    /// <summary>
    /// Draws chunk columns and picks the surface, above, sky, sea-floor, hell and underground candidate
    /// chunks from them, then splits the underground candidates by their carved-cell count into
    /// cave-heavy (most carved) and deep-stone (least carved). Untimed.
    /// </summary>
    private static List<Vector3>[] SelectChunks()
    {
        var classes = new List<Vector3>[ChunkClassNames.Length];
        for (var c = 0; c < classes.Length; c++)
            classes[c] = [];
        var underground = new List<Vector3>();
        var scratch = new WorldgenStageTimer();

        var state = ChunkSeed;
        for (var draw = 0; draw < MaxChunkDraws; draw++)
        {
            if (ChunkClassesFull(classes, underground))
                break;

            var (cx, cz) = NextChunkColumn(ref state);
            var column = ColumnCache.Get(cx, cz);
            var center = column.Biomes[(8 << Chunk.SizeShift) | 8];
            var top = column.MaxHeight >> Chunk.SizeShift;

            if (center == Biome.Ocean)
            {
                if (column.MaxHeight < WorldGenerationConfig.SeaLevel)
                    TryAdd(classes[SeaFloor], PositionsPerClass, new Vector3(cx, top, cz));
                continue;
            }

            if (column.MinHeight < WorldGenerationConfig.SeaLevel)
                continue;

            TryAdd(classes[Surface], PositionsPerClass, new Vector3(cx, top, cz));
            TryAdd(classes[Above], PositionsPerClass, new Vector3(cx, top + 1, cz));

            if (classes[Sky].Count < SkyPositions)
            {
                // Keep only a chunk that really takes the early-out (marks no fill)
                var sky = new Vector3(cx, top + 8, cz);
                scratch.Clear();
                ChunkTerrainFill.GenerateBlockData(new Chunk(null!, sky, generateBlockData: false), scratch);
                if (scratch.MarksOf(WorldgenStageTimer.Stage.Fill) == 0)
                    classes[Sky].Add(sky);
            }

            var hellCy = (WorldGenerationConfig.HellTopY >> Chunk.SizeShift) - 1
                         - (int)(NextRandom(ref state) % HellChunkLayers);
            TryAdd(classes[Hell], PositionsPerClass, new Vector3(cx, hellCy, cz));

            if (underground.Count < UndergroundCandidates)
            {
                var k = 2 + (int)(NextRandom(ref state) % 4);
                underground.Add(new Vector3(cx, (column.MinHeight >> Chunk.SizeShift) - k, cz));
            }
        }

        var ranked = new List<(int Carved, Vector3 Pos)>(underground.Count);
        foreach (var pos in underground)
            ranked.Add((CarvedCells(pos), pos));
        ranked.Sort((a, b) =>
        {
            if (a.Carved != b.Carved)
                return b.Carved.CompareTo(a.Carved);
            if (a.Pos.X != b.Pos.X)
                return a.Pos.X.CompareTo(b.Pos.X);
            if (a.Pos.Z != b.Pos.Z)
                return a.Pos.Z.CompareTo(b.Pos.Z);
            return a.Pos.Y.CompareTo(b.Pos.Y);
        });

        // Halve a short candidate list so the two classes never share a chunk
        var take = Math.Min(PositionsPerClass, ranked.Count / 2);
        for (var i = 0; i < take; i++)
        {
            classes[CaveHeavy].Add(ranked[i].Pos);
            classes[DeepStone].Add(ranked[ranked.Count - 1 - i].Pos);
        }

        return classes;
    }

    private static bool ChunkClassesFull(List<Vector3>[] classes, List<Vector3> underground) =>
        classes[Surface].Count >= PositionsPerClass
        && classes[Above].Count >= PositionsPerClass
        && classes[Sky].Count >= SkyPositions
        && classes[SeaFloor].Count >= PositionsPerClass
        && classes[Hell].Count >= PositionsPerClass
        && underground.Count >= UndergroundCandidates;

    private static void TryAdd(List<Vector3> list, int target, Vector3 pos)
    {
        if (list.Count < target)
            list.Add(pos);
    }

    /// <summary>Cells at or below the column's terrain height that the generated chunk leaves non-solid.</summary>
    private static int CarvedCells(Vector3 pos)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk);
        var column = ColumnCache.Get((int)pos.X, (int)pos.Z);
        var baseY = (int)pos.Y << Chunk.SizeShift;

        var carved = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            for (var y = 0; y < Chunk.Size; y++)
            {
                if (baseY + y <= height && !BlockType.IsSolid(chunk.GetLocalBlock(x, y, z)))
                    carved++;
            }
        }

        return carved;
    }

    /// <summary>
    /// Generates a fresh chunk at <paramref name="pos"/> through the stage timer, records its whole
    /// and per-stage time as the next sample and returns its solid count. The only allocation is the
    /// chunk, which production allocates too.
    /// </summary>
    private static int TimeChunk(Vector3 pos, WorldgenStageTimer timer, ChunkSamples into)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        timer.Clear();
        var t0 = Stopwatch.GetTimestamp();
        ChunkTerrainFill.GenerateBlockData(chunk, timer);
        var whole = Stopwatch.GetTimestamp() - t0;

        var i = into.Count++;
        into.Whole[i] = TicksToMs(whole);
        for (var s = 0; s < WorldgenStageTimer.StageCount; s++)
            into.Stage[s][i] = TicksToMs(timer.TicksOf((WorldgenStageTimer.Stage)s));
        return chunk.SolidCount;
    }

    /// <summary>Prints one sample set: the whole call, the stage sum against it, and every stage by share.</summary>
    private void Report(string label, ChunkSamples samples)
    {
        var n = samples.Count;
        if (n == 0)
        {
            _output.WriteLine($"  {label}: no samples");
            return;
        }

        var (med, p90, mean) = Stats(samples.Whole, n);
        var sumMean = 0.0;
        for (var s = 0; s < WorldgenStageTimer.StageCount; s++)
        for (var i = 0; i < n; i++)
            sumMean += samples.Stage[s][i];
        sumMean /= n;

        _output.WriteLine(
            $"  {label}: {n} samples  whole med {med:F3} p90 {p90:F3} mean {mean:F3} ms/chunk  " +
            $"stage sum mean {sumMean:F3} ms (gap {(mean - sumMean) * 1000.0:F1} us)");

        var rows = new List<(WorldgenStageTimer.Stage Stage, double Median, double P90, double Mean)>();
        for (var s = 0; s < WorldgenStageTimer.StageCount; s++)
        {
            var (sMed, sP90, sMean) = Stats(samples.Stage[s], n);
            if (sMean > 0)
                rows.Add(((WorldgenStageTimer.Stage)s, sMed, sP90, sMean));
        }

        rows.Sort((a, b) => b.Mean.CompareTo(a.Mean));
        foreach (var row in rows)
        {
            var perColumn = row.Stage == WorldgenStageTimer.Stage.Fill
                ? $"  ({row.Mean * 1000.0 / (Chunk.Size * Chunk.Size):F2} us/column)"
                : "";
            _output.WriteLine(
                $"    {row.Stage,-16} med {row.Median:F3} p90 {row.P90:F3} mean {row.Mean:F3} ms  " +
                $"{row.Mean / mean * 100.0,5:F1}%{perColumn}");
        }
    }

    /// <summary>Preallocated whole and per-stage times (ms) of one class's samples.</summary>
    private sealed class ChunkSamples
    {
        public readonly double[] Whole;
        public readonly double[][] Stage;
        public int Count;

        public ChunkSamples(int capacity)
        {
            Whole = new double[capacity];
            Stage = new double[WorldgenStageTimer.StageCount][];
            for (var s = 0; s < Stage.Length; s++)
                Stage[s] = new double[capacity];
        }

        public static ChunkSamples Concat(ChunkSamples[] parts)
        {
            var total = 0;
            foreach (var part in parts)
                total += part.Count;

            var all = new ChunkSamples(total);
            foreach (var part in parts)
            {
                Array.Copy(part.Whole, 0, all.Whole, all.Count, part.Count);
                for (var s = 0; s < all.Stage.Length; s++)
                    Array.Copy(part.Stage[s], 0, all.Stage[s], all.Count, part.Count);
                all.Count += part.Count;
            }

            return all;
        }
    }

    /// <summary>
    /// A probe-local SplitMix64, so the drawn columns are a fixed workload rather than whatever a
    /// runtime or a production generator happens to produce today.
    /// </summary>
    private static ulong NextRandom(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A chunk column in [-1250, 1250) on both axes, about 20000 blocks either way.</summary>
    private static (int Cx, int Cz) NextChunkColumn(ref ulong state)
    {
        var cx = (int)(NextRandom(ref state) % 2500) - 1250;
        var cz = (int)(NextRandom(ref state) % 2500) - 1250;
        return (cx, cz);
    }

    /// <summary>Median, p90 and mean of the first <paramref name="count"/> samples.</summary>
    private static (double Median, double P90, double Mean) Stats(double[] ms, int count)
    {
        var sorted = new double[count];
        Array.Copy(ms, sorted, count);
        Array.Sort(sorted);
        var sum = 0.0;
        foreach (var v in sorted)
            sum += v;
        var median = count % 2 == 1
            ? sorted[count / 2]
            : (sorted[count / 2 - 1] + sorted[count / 2]) * 0.5;
        var p90 = sorted[(int)Math.Ceiling(0.9 * count) - 1];
        return (median, p90, sum / count);
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Empties every worldgen cache and collects the dropped objects. Always untimed.</summary>
    private static void ResetCold()
    {
        WorldSeed.Apply(WorldSeed.Default);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
