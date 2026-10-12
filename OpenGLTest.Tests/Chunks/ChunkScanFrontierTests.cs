using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// Pins <see cref="ChunkScanFrontier"/>, the shared resume point the four chunk-generation workers
/// use instead of restarting the ~17k-entry sorted-offset scan on every call (PERF_TODO C3).
///
/// <para>The oracle is <see cref="Reference"/>: the pre-change full scan, re-expressed verbatim.
/// Every equivalence fact asserts the frontier's answer against it, over the real
/// <see cref="ChunkGenerationWorker.SortedOffsets"/> array. The needed-set is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of claimed positions with
/// <c>needs = p =&gt; !claimed.ContainsKey(p)</c> - a faithful stand-in for
/// <see cref="ChunkManager.NeedsChunk"/> (itself two dictionary probes) that runs in milliseconds
/// instead of generating real world data.</para>
///
/// <para><b>What the concurrency facts prove, and what they do not.</b> They demonstrate the absence
/// of the races they happen to exercise, on this machine, under this scheduler, for this number of
/// iterations. They are not a proof that the design is race-free: that argument is the invariant
/// written on <see cref="ChunkScanFrontier"/> itself, and these facts are a check on it, not a
/// substitute for it. A genuinely racy variant that simply never interleaves badly here would pass
/// them.</para>
///
/// <para><b>Two threads returning the same position is expected and benign</b>, so no fact asserts
/// that returns are distinct. The frontier is an optimization over a scan, not a work queue;
/// duplicate hand-outs are deduped downstream by <c>QueueChunk</c>'s
/// <c>_queuedChunkPositions.TryAdd</c> (ChunkManager.cs:574), exactly as they were before this
/// change, when four threads independently scanning the same list routinely picked the same chunk.
/// The concurrency facts therefore assert coverage (every offset ends up claimed exactly once, by
/// somebody) rather than any particular assignment of offsets to threads.</para>
/// </summary>
public sealed class ChunkScanFrontierTests
{
    private static readonly Vector3[] Offsets = ChunkGenerationWorker.SortedOffsets;

    private static ChunkScanFrontier NewFrontier() => new(Offsets);

    /// <summary>
    /// The pre-C3 scan, verbatim: walk the closest-first offsets from the center and return the
    /// first needed position. This is the behavior the frontier must reproduce.
    /// </summary>
    private static Vector3? Reference(Vector3 center, Func<Vector3, bool> isNeeded)
    {
        foreach (var offset in Offsets)
        {
            var pos = center + offset;
            if (isNeeded(pos))
                return pos;
        }

        return null;
    }

    /// <summary>
    /// The equivalence fact: over a long single-threaded run that fills a world, moves the camera
    /// center and frees already-claimed positions (bumping the removal version as
    /// UnloadDistantChunks does), the frontier's answer equals the full scan's answer on every
    /// single call. Phase A fills from an empty world; phase B settles the world completely and then
    /// frees one position at a random index at a time, which is what produces the null answers the
    /// guard requires - 2000 rounds of pure filling would never exhaust a 17k-offset sphere.
    /// </summary>
    [Fact]
    public void FrontierReturnsExactlyWhatAFullScanWouldReturn()
    {
        var claimed = new ConcurrentDictionary<Vector3, byte>();
        Func<Vector3, bool> needs = p => !claimed.ContainsKey(p);
        var frontier = NewFrontier();
        var rng = new Random(20260912);

        // Far enough apart that the three centers' offset spheres cannot overlap.
        var centers = new[] { Vector3.Zero, new Vector3(1000f, 0f, 0f), new Vector3(2000f, 0f, 0f) };
        var centerIndex = 0;
        var center = centers[0];
        var version = 0;
        var claimedOrder = new List<Vector3>();
        var sawHit = false;
        var sawNull = false;

        void Round()
        {
            var expected = Reference(center, needs);
            var actual = frontier.FindNext(center, version, needs);
            Assert.Equal(expected, actual);

            if (actual.HasValue)
            {
                sawHit = true;
                if (claimed.TryAdd(actual.Value, 0))
                    claimedOrder.Add(actual.Value);
            }
            else
            {
                sawNull = true;
            }
        }

        for (var round = 0; round < 2000; round++)
        {
            Round();

            if ((round + 1) % 150 == 0)
            {
                centerIndex = (centerIndex + 1) % centers.Length;
                center = centers[centerIndex];
            }

            if ((round + 1) % 300 == 0 && claimedOrder.Count > 0)
            {
                var victim = claimedOrder[rng.Next(claimedOrder.Count)];
                if (claimed.TryRemove(victim, out _))
                {
                    claimedOrder.Remove(victim);
                    // The bump lands after the removal, the ordering UnloadDistantChunks uses.
                    version++;
                }
            }
        }

        // Phase B: settle the world around the current center, then free one position at a time.
        foreach (var offset in Offsets)
            claimed.TryAdd(center + offset, 0);

        for (var i = 0; i < 200; i++)
        {
            Round();

            var victim = center + Offsets[rng.Next(Offsets.Length)];
            if (claimed.TryRemove(victim, out _))
                version++;

            Round();
        }

        Assert.True(sawHit, "no round ever returned a position; the fact would be vacuous");
        Assert.True(sawNull, "no round ever returned null; the converged path was never exercised");
    }

    /// <summary>
    /// The precondition, stated as a fact: the frontier trusts its proved prefix until the removal
    /// version moves. Freeing a low-index position WITHOUT bumping leaves the frontier answering
    /// null even though a full scan would find it - which is exactly why
    /// <see cref="ChunkManager.ChunkRemovalVersion"/> must be bumped by every path that can make a
    /// position needed again. Bumping then produces that exact position. This is the fact that fails
    /// if someone drops the bump, or moves it ahead of the removals it announces.
    /// </summary>
    [Fact]
    public void FrontierTrustsThePrefixUntilTheRemovalVersionMoves()
    {
        var claimed = new ConcurrentDictionary<Vector3, byte>();
        Func<Vector3, bool> needs = p => !claimed.ContainsKey(p);
        var frontier = NewFrontier();
        var center = new Vector3(7f, -3f, 12f);
        foreach (var offset in Offsets)
            claimed.TryAdd(center + offset, 0);

        var version = 5;

        Assert.Null(frontier.FindNext(center, version, needs));
        Assert.Equal(frontier.OffsetCount, frontier.PublishedIndex);

        var freed = center + Offsets[3];
        Assert.True(claimed.TryRemove(freed, out _));

        // A full scan would find it...
        Assert.Equal<Vector3?>(freed, Reference(center, needs));
        // ...and the frontier deliberately does not, until it is told a removal happened.
        Assert.Null(frontier.FindNext(center, version, needs));

        version++;
        Assert.Equal<Vector3?>(freed, frontier.FindNext(center, version, needs));
    }

    /// <summary>
    /// A center that does not match the published node resets the scan to the closest offset. The
    /// second center has its nearest 40 offsets pre-claimed so a non-zero index is genuinely reached
    /// and published; the first is checked only against the oracle, because a hit at index 0
    /// publishes nothing by design (Publish early-returns when index &lt;= start), which is also why
    /// this fact keys on <see cref="ChunkScanFrontier.PublishedCenter"/> rather than on
    /// <see cref="ChunkScanFrontier.PublishedIndex"/>.
    /// </summary>
    [Fact]
    public void AMovedCenterIsScannedFromTheClosestOffset()
    {
        var claimed = new ConcurrentDictionary<Vector3, byte>();
        Func<Vector3, bool> needs = p => !claimed.ContainsKey(p);
        var frontier = NewFrontier();
        const int version = 11;

        var a = new Vector3(-40f, 5f, 90f);
        foreach (var offset in Offsets)
            claimed.TryAdd(a + offset, 0);

        Assert.Null(frontier.FindNext(a, version, needs));
        Assert.Equal<Vector3?>(a, frontier.PublishedCenter);

        // Nothing claimed around b1: the answer is the closest offset of all.
        var b1 = a + new Vector3(100f, 0f, 0f);
        var r1 = frontier.FindNext(b1, version, needs);
        Assert.Equal(Reference(b1, needs), r1);
        Assert.Equal<Vector3?>(b1 + Offsets[0], r1);

        // The nearest 40 around b2 are taken, so the scan has to walk into the list and publish.
        var b2 = a + new Vector3(200f, 0f, 0f);
        for (var i = 0; i < 40; i++)
            claimed.TryAdd(b2 + Offsets[i], 0);

        var r2 = frontier.FindNext(b2, version, needs);
        Assert.Equal(Reference(b2, needs), r2);
        Assert.Equal<Vector3?>(b2 + Offsets[40], r2);
        Assert.Equal<Vector3?>(b2, frontier.PublishedCenter);
    }

    /// <summary>
    /// Coverage under contention: four threads sharing one frontier, as the real workers do, between
    /// them claim every offset in the sphere exactly once. Duplicate hand-outs are expected (see the
    /// class doc) and are not asserted against; what must hold is that nothing is left behind and
    /// that the run terminates. A thread that is handed null re-checks rather than exiting, so a
    /// frontier that wrongly reported "nothing needed" would burn the deadline and fail loudly
    /// instead of passing quietly.
    /// </summary>
    [Fact]
    public void FourThreadsBetweenThemClaimEveryOffset()
    {
        var claimed = new ConcurrentDictionary<Vector3, byte>();
        Func<Vector3, bool> needs = p => !claimed.ContainsKey(p);
        var frontier = NewFrontier();
        var center = new Vector3(3f, 0f, -8f);
        const int version = 2;
        const int deadlineMs = 30_000;

        var successfulClaims = 0;
        var clock = Stopwatch.StartNew();
        var threads = new Thread[4];

        for (var t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                while (Volatile.Read(ref successfulClaims) < frontier.OffsetCount &&
                       clock.ElapsedMilliseconds < deadlineMs)
                {
                    var pos = frontier.FindNext(center, version, needs);
                    if (pos is null)
                    {
                        Thread.Yield();
                        continue;
                    }

                    if (claimed.TryAdd(pos.Value, 0))
                        Interlocked.Increment(ref successfulClaims);
                }
            })
            {
                IsBackground = true,
                Name = $"FrontierStress-{t}"
            };
            threads[t].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.True(clock.ElapsedMilliseconds < deadlineMs,
            $"the fill did not finish within {deadlineMs} ms ({successfulClaims}/{frontier.OffsetCount} claimed)");
        Assert.Equal(frontier.OffsetCount, successfulClaims);
        Assert.Equal(frontier.OffsetCount, claimed.Count);

        foreach (var offset in Offsets)
            Assert.True(claimed.ContainsKey(center + offset), $"offset {offset} was never claimed");
    }

    /// <summary>
    /// The starvation case the round exists to rule out, under load: with four threads hammering a
    /// settled frontier, a driver repeatedly frees a position well BEHIND the published index and
    /// bumps the version afterwards (the ordering UnloadDistantChunks uses). Every freed position
    /// must be picked up again. A cursor that only moves forward, or one that trusted its prefix
    /// across a removal, would leave entries outstanding here until the deadline.
    /// </summary>
    [Fact]
    public void AChunkFreedBehindTheFrontierIsStillPickedUpUnderLoad()
    {
        var claimed = new ConcurrentDictionary<Vector3, byte>();
        Func<Vector3, bool> needs = p => !claimed.ContainsKey(p);
        var frontier = NewFrontier();
        var center = new Vector3(-60f, 4f, 17f);
        foreach (var offset in Offsets)
            claimed.TryAdd(center + offset, 0);

        var version = 40;
        var running = true;
        var reclaims = 0;
        var outstanding = new ConcurrentDictionary<Vector3, byte>();
        const int deadlineMs = 30_000;

        var workers = new Thread[4];
        for (var t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(() =>
            {
                while (Volatile.Read(ref running))
                {
                    var pos = frontier.FindNext(center, Volatile.Read(ref version), needs);
                    if (pos is null)
                    {
                        Thread.Yield();
                        continue;
                    }

                    if (claimed.TryAdd(pos.Value, 0) && outstanding.TryRemove(pos.Value, out _))
                        Interlocked.Increment(ref reclaims);
                }
            })
            {
                IsBackground = true,
                Name = $"FrontierFreeStress-{t}"
            };
            workers[t].Start();
        }

        var rng = new Random(4242);
        var freedCount = 0;
        for (var i = 0; i < 100; i++)
        {
            // Low index: comfortably behind the frontier, which sits at the end of the list.
            var victim = center + Offsets[rng.Next(200)];

            // Marked outstanding BEFORE it becomes needed, so a worker cannot claim it back in the
            // window between the removal and the bookkeeping and leave a phantom entry behind.
            outstanding[victim] = 0;
            if (claimed.TryRemove(victim, out _))
            {
                freedCount++;
                Interlocked.Increment(ref version);
            }
            else
            {
                outstanding.TryRemove(victim, out _);
            }

            Thread.Sleep(1);
        }

        var clock = Stopwatch.StartNew();
        while (!outstanding.IsEmpty && clock.ElapsedMilliseconds < deadlineMs)
            Thread.Sleep(1);

        Volatile.Write(ref running, false);
        foreach (var worker in workers)
            worker.Join();

        Assert.True(freedCount > 0, "nothing was ever freed; the fact would be vacuous");
        Assert.True(outstanding.IsEmpty,
            $"{outstanding.Count} freed position(s) were never picked up again within {deadlineMs} ms");
        Assert.Equal(freedCount, reclaims);
    }

    /// <summary>
    /// The point of the change: the scan cost is paid once across a fill, not once per call.
    /// Deliberately single-threaded - a descheduled thread resuming on a stale node legitimately
    /// rescans a whole prefix, so any multi-threaded probe bound would be scheduler-dependent and
    /// flaky. Each index is probed at most twice (once when the scan walks past it, once when the
    /// next call re-tests the offset it returned, since a hit publishes i and not i + 1), so the
    /// total sits just above 2x the offset count. The pre-change scan probed the whole list on every
    /// call, which over a full fill is four orders of magnitude above this bound.
    /// </summary>
    [Fact]
    public void TheScanCostIsAmortizedNotPerCall()
    {
        var claimed = new ConcurrentDictionary<Vector3, byte>();
        var probes = 0;
        Func<Vector3, bool> needs = p =>
        {
            probes++;
            return !claimed.ContainsKey(p);
        };

        var frontier = NewFrontier();
        var center = new Vector3(12f, 1f, -5f);
        const int version = 3;

        var calls = 0;
        while (true)
        {
            var pos = frontier.FindNext(center, version, needs);
            calls++;
            if (pos is null) break;
            Assert.True(claimed.TryAdd(pos.Value, 0), $"{pos.Value} was handed out twice");
        }

        Assert.Equal(frontier.OffsetCount, claimed.Count);
        Assert.Equal(frontier.OffsetCount + 1, calls);
        Assert.True(probes < 3 * frontier.OffsetCount,
            $"{probes} probes for {frontier.OffsetCount} offsets across {calls} calls");
    }
}
