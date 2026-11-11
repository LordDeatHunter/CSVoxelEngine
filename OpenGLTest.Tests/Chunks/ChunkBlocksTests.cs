using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;
using static VoxelEngine.Chunks.ChunkBlocks;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// Pins <see cref="ChunkBlocks"/>, the palette-compressed id storage of a chunk. The type is GL-free
/// and content-free, so every fact runs on private instances against a plain <c>ushort[]</c> oracle
/// with ids drawn from the whole ushort range; no <c>TestContent</c> is needed and nothing is shared
/// between facts.
///
/// <para><b>The negative control for each fact</b>, i.e. the source mutation it is here to catch:
/// <list type="bullet">
/// <item><see cref="RandomWritesAgreeWithADenseOracle"/> - a wrong slot appended or looked up, an
/// upgrade that drops a cell, a <c>CopyRun</c> window off by one, an air count that misses the air
/// slot, a wrong <c>previous</c> id, or a palette count published short (an id missing from
/// <c>IdSpan</c>).</item>
/// <item><see cref="TheModeNeverMovesBackward"/> - a write that allocates a new object without a
/// mode change (a same-id write that copies, or an in-mode write that rebuilds), which would make
/// the owner swap references on every edit.</item>
/// <item><see cref="FromDensePicksTheSmallestModeThatHoldsTheDistinctIds"/> - an off-by-one at the
/// palette capacity, a uniform check that misses a single odd cell, or a palette that repeats an id
/// across runs.</item>
/// <item><see cref="FromDenseLeavesNoStateBehindBetweenCalls"/> - the thread-static reverse lookup
/// not reset (or not reset on the bail to Direct), so a later call reuses a stale slot.</item>
/// <item><see cref="OnlyAnUnwrittenFromDenseResultIsCompact"/> - a write that keeps the compact flag,
/// which would let a restore clone an edited snapshot's stale palette into a live chunk.</item>
/// <item><see cref="CloneIsIndependentOfTheOriginal"/> - a clone that shares the palette, index or
/// direct array with its source.</item>
/// <item><see cref="AConcurrentReaderOnlyObservesValuesWrittenToThatCell"/> - the index published
/// before its palette slot, or an upgrade that mutates the old object in place instead of building
/// a new one.</item>
/// </list></para>
///
/// <para><b>What the concurrency fact proves, and what it does not.</b> It demonstrates the absence
/// of the races it happens to exercise, on this machine (x86/x64, strongly ordered), for its
/// deadline. The argument for weakly ordered CPUs is the numbered invariant list on
/// <see cref="ChunkBlocks"/>; this fact is a check on it, not a substitute.</para>
/// </summary>
public sealed class ChunkBlocksTests
{
    private const int Cells = Chunk.BlockCount;

    /// <summary><paramref name="count"/> distinct non-air ids drawn from the whole ushort range.</summary>
    private static ushort[] DistinctIds(Random rng, int count, ISet<ushort>? exclude = null)
    {
        var picked = new HashSet<ushort>();
        var ids = new List<ushort>(count);
        while (ids.Count < count)
        {
            var id = (ushort)rng.Next(1, ushort.MaxValue + 1);
            if (exclude != null && exclude.Contains(id))
                continue;
            if (picked.Add(id))
                ids.Add(id);
        }
        return [.. ids];
    }

    /// <summary>A dense chunk using every id of <paramref name="ids"/> at least once, in shuffled cells.</summary>
    private static ushort[] DenseOf(Random rng, IReadOnlyList<ushort> ids)
    {
        var dense = new ushort[Cells];
        for (var i = 0; i < Cells; i++)
            dense[i] = ids[i % ids.Count];
        rng.Shuffle(dense);
        return dense;
    }

    private static StorageMode SmallestModeFor(int distinct) =>
        distinct == 1 ? StorageMode.Uniform
        : distinct <= PaletteCapacity ? StorageMode.Indexed
        : StorageMode.Direct;

    private static void AssertMatches(ushort[] oracle, ChunkBlocks blocks, Random rng)
    {
        for (var i = 0; i < Cells; i++)
            Assert.Equal(oracle[i], blocks.Get(i));
        Assert.Equal(oracle, blocks.ToDense());

        var window = new ushort[Cells];
        for (var w = 0; w < 32; w++)
        {
            var start = rng.Next(Cells);
            var length = rng.Next(Cells - start + 1);
            blocks.CopyRun(start, length, window);
            Assert.True(oracle.AsSpan(start, length).SequenceEqual(window.AsSpan(0, length)),
                $"CopyRun({start}, {length}) disagrees with the oracle");
        }

        Assert.Equal(oracle.Count(id => id != 0), blocks.CountNonAir());
        var held = new HashSet<ushort>(blocks.IdSpan.ToArray());
        Assert.True(held.IsSupersetOf(oracle), "IdSpan is missing an id the cells hold");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(40)]
    [InlineData(PaletteCapacity)]
    [InlineData(PaletteCapacity + 1)]
    [InlineData(1500)]
    public void RandomWritesAgreeWithADenseOracle(int distinctBudget)
    {
        var rng = new Random(9100 + distinctBudget);
        // The pool always holds air, the id a fresh storage starts from, so the budget counts it.
        var pool = new ushort[] { 0 }.Concat(DistinctIds(rng, distinctBudget - 1)).ToArray();
        var oracle = new ushort[Cells];
        var blocks = Air;
        var everWritten = new HashSet<ushort> { 0 };

        for (var batch = 0; batch < 6; batch++)
        {
            for (var w = 0; w < 1500; w++)
            {
                var i = rng.Next(Cells);
                var id = pool[rng.Next(pool.Length)];
                blocks = blocks.Set(i, id, out var previous);
                Assert.Equal(oracle[i], previous);
                oracle[i] = id;
                everWritten.Add(id);
            }

            AssertMatches(oracle, blocks, rng);
        }

        // Runtime storage never compacts, so its mode follows every id it has ever stored.
        Assert.Equal(SmallestModeFor(everWritten.Count), blocks.Mode);
    }

    [Fact]
    public void TheModeNeverMovesBackward()
    {
        var rng = new Random(9200);
        var ids = DistinctIds(rng, PaletteCapacity + 64);
        var blocks = Uniform(ids[0]);
        var seen = new HashSet<StorageMode>();

        for (var w = 0; w < 4000; w++)
        {
            var i = rng.Next(Cells);
            var current = blocks.Get(i);
            Assert.Same(blocks, blocks.Set(i, current, out _));

            var id = ids[Math.Min(w / 8, ids.Length - 1)];
            var next = blocks.Set(i, id, out _);
            Assert.True(next.Mode >= blocks.Mode, $"mode moved back from {blocks.Mode} to {next.Mode}");
            Assert.Equal(next.Mode != blocks.Mode, !ReferenceEquals(next, blocks));
            blocks = next;
            seen.Add(blocks.Mode);
        }

        Assert.Contains(StorageMode.Indexed, seen);
        Assert.Contains(StorageMode.Direct, seen);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(PaletteCapacity)]
    [InlineData(PaletteCapacity + 1)]
    [InlineData(Cells)]
    public void FromDensePicksTheSmallestModeThatHoldsTheDistinctIds(int distinct)
    {
        var rng = new Random(9300 + distinct);
        var dense = DenseOf(rng, DistinctIds(rng, distinct));
        var d = dense.Distinct().Count();

        var blocks = FromDense(dense);

        Assert.Equal(SmallestModeFor(d), blocks.Mode);
        Assert.Equal(dense, blocks.ToDense());
        if (blocks.Mode != StorageMode.Indexed)
            return;
        var palette = blocks.IdSpan.ToArray();
        Assert.Equal(d, palette.Length);
        Assert.Equal(d, palette.Distinct().Count());
    }

    [Fact]
    public void FromDenseLeavesNoStateBehindBetweenCalls()
    {
        var rng = new Random(9400);
        var small = DistinctIds(rng, 12);
        var wide = DistinctIds(rng, PaletteCapacity + 40, new HashSet<ushort>(small));

        // Later calls reuse ids the earlier ones put in the thread-static lookup, in a new order, so
        // any slot left behind (including by the bail to Direct) would be read back here.
        var mixed = wide.Take(100).Reverse().Concat(small.Skip(3)).ToArray();
        var reversed = small.Reverse().ToArray();
        foreach (var ids in new[] { small, wide, mixed, reversed })
        {
            var dense = DenseOf(rng, ids);
            var blocks = FromDense(dense);
            Assert.Equal(dense, blocks.ToDense());
            Assert.Equal(SmallestModeFor(ids.Length), blocks.Mode);
            if (blocks.Mode == StorageMode.Indexed)
                Assert.Equal(ids.Length, blocks.IdSpan.ToArray().Distinct().Count());
        }
    }

    [Fact]
    public void OnlyAnUnwrittenFromDenseResultIsCompact()
    {
        var rng = new Random(9500);
        Assert.True(Air.IsCompact);
        Assert.True(Uniform(7).IsCompact);

        var uniformUpgrade = Uniform(7).Set(0, 8, out _);
        Assert.False(uniformUpgrade.IsCompact);

        var indexed = FromDense(DenseOf(rng, DistinctIds(rng, 3)));
        var direct = FromDense(DenseOf(rng, DistinctIds(rng, PaletteCapacity + 1)));
        var full = FromDense(DenseOf(rng, DistinctIds(rng, PaletteCapacity)));
        foreach (var blocks in new[] { indexed, direct, full })
        {
            Assert.True(blocks.IsCompact);
            Assert.Same(blocks, blocks.Set(5, blocks.Get(5), out _));
            Assert.True(blocks.IsCompact, "a write that changed nothing cleared the flag");
            Assert.True(blocks.Clone().IsCompact);
        }

        var fresh = DistinctIds(rng, 1, new HashSet<ushort>(full.IdSpan.ToArray()))[0];
        var fullUpgrade = full.Set(5, fresh, out _);
        Assert.Equal(StorageMode.Direct, fullUpgrade.Mode);
        Assert.False(fullUpgrade.IsCompact);

        foreach (var blocks in new[] { indexed, direct })
        {
            // An id the storage already holds, so the write stays in mode for Indexed too.
            var other = blocks.IdSpan.ToArray().First(id => id != blocks.Get(0));
            Assert.Same(blocks, blocks.Set(0, other, out _));
            Assert.False(blocks.IsCompact);
            Assert.False(blocks.Clone().IsCompact);
        }
    }

    [Fact]
    public void CloneIsIndependentOfTheOriginal()
    {
        var rng = new Random(9600);
        foreach (var distinct in new[] { 5, PaletteCapacity + 1 })
        {
            var dense = DenseOf(rng, DistinctIds(rng, distinct));
            var original = FromDense(dense);
            var clone = original.Clone();

            var writes = DistinctIds(rng, 50, new HashSet<ushort>(dense));
            for (var w = 0; w < writes.Length; w++)
                original = original.Set(rng.Next(Cells), writes[w], out _);

            Assert.Equal(dense, clone.ToDense());
            Assert.NotEqual(dense, original.ToDense());
        }

        var uniform = Uniform(42);
        Assert.Equal(uniform.ToDense(), uniform.Clone().ToDense());
    }

    /// <summary>The owner's side of invariant 5, exactly as <see cref="Chunk"/> publishes an upgrade.</summary>
    private sealed class Holder
    {
        public volatile ChunkBlocks Blocks;

        public Holder(ChunkBlocks start) => Blocks = start;

        public StorageMode Write(int i, ushort id)
        {
            var blocks = Blocks;
            var next = blocks.Set(i, id, out _);
            if (!ReferenceEquals(next, blocks))
                Blocks = next;
            return next.Mode;
        }
    }

    [Fact]
    public void AConcurrentReaderOnlyObservesValuesWrittenToThatCell()
    {
        // Cell i is only ever written 1 + i + Cells * k, so every value names its cell; 0 is never
        // written, so a reader that follows an index to an unset palette slot reads a 0 and fails.
        const ushort initial = ushort.MaxValue;
        const int idsPerCell = 15;
        static bool Belongs(ushort v, int i) => v == initial || (v != 0 && (v - 1) % Cells == i);

        var holder = new Holder(Uniform(initial));
        var failures = new ConcurrentBag<string>();
        var done = 0;
        long reads = 0;
        var sawWrittenValue = 0;
        var toIndexed = 0;
        var toDirect = 0;

        var writer = new Thread(() =>
        {
            var rng = new Random(9700);
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 1500)
            {
                holder.Blocks = Uniform(initial);
                var mode = StorageMode.Uniform;
                // Few cells first (in-mode Indexed writes), then enough distinct ids to pass the capacity.
                for (var w = 0; w < 600; w++)
                {
                    var i = rng.Next(w < 150 ? 64 : Cells);
                    var id = (ushort)(1 + i + Cells * rng.Next(idsPerCell));
                    var next = holder.Write(i, id);
                    if (mode == StorageMode.Uniform && next == StorageMode.Indexed)
                        toIndexed++;
                    if (mode == StorageMode.Indexed && next == StorageMode.Direct)
                        toDirect++;
                    mode = next;
                }
            }
            Volatile.Write(ref done, 1);
        });

        var readers = Enumerable.Range(0, 3).Select(r => new Thread(() =>
        {
            var rng = new Random(9800 + r);
            var row = new ushort[Chunk.Size];
            long local = 0;
            var sawWritten = false;
            while (Volatile.Read(ref done) == 0)
            {
                var blocks = holder.Blocks;
                var i = rng.Next(Cells);
                var v = blocks.Get(i);
                local++;
                sawWritten |= v != initial;
                if (!Belongs(v, i))
                    failures.Add($"Get({i}) = {v} in {blocks.Mode}");

                var start = rng.Next(Cells / Chunk.Size) * Chunk.Size;
                blocks.CopyRun(start, Chunk.Size, row);
                for (var k = 0; k < row.Length; k++)
                {
                    local++;
                    if (!Belongs(row[k], start + k))
                        failures.Add($"CopyRun cell {start + k} = {row[k]} in {blocks.Mode}");
                }
            }
            Interlocked.Add(ref reads, local);
            if (sawWritten)
                Interlocked.Exchange(ref sawWrittenValue, 1);
        })).ToArray();

        foreach (var reader in readers)
            reader.Start();
        writer.Start();
        writer.Join();
        foreach (var reader in readers)
            reader.Join();

        Assert.Empty(failures.Take(10));
        Assert.True(Interlocked.Read(ref reads) > 100_000, $"only {reads} reads ran");
        Assert.True(toIndexed > 0 && toDirect > 0, $"upgrades: {toIndexed} to Indexed, {toDirect} to Direct");
        Assert.Equal(1, sawWrittenValue);
    }
}
