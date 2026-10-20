using System;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Streaming;
using VoxelEngine.Player;

namespace VoxelEngine.Lod;

/// <summary>LodManager's pool of <see cref="StreamingConfig.MaxConcurrentLodGenerations"/> BelowNormal LodGen threads: starting it (refused while a straggler from a timed-out stop lives), the worker loop and its idle, throttle and error back-offs, and the joining stop.</summary>
internal sealed class LodWorkerPool(LodManager owner, LodRegionClaims claims)
{
    private readonly LodManager _owner = owner;
    private readonly LodRegionClaims _claims = claims;
    private volatile bool _isRunning;

    // The threads the last Start spawned, kept until a Stop has joined every one of them so
    // AnyThreadAlive stays truthful after a timed-out join and a later Stop can re-join.
    private Thread[]? _threads;

    // The workers' idle, throttle and error back-offs wait on this instead of Thread.Sleep, so
    // Stop can cut a sleeping worker's wait short and the join returns promptly.
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>
    /// Whether any thread from the last <see cref="Start"/> is still running. False once
    /// <see cref="Stop"/> has returned true.
    /// </summary>
    internal bool AnyThreadAlive
    {
        get
        {
            var threads = _threads;
            if (threads == null) return false;
            foreach (var thread in threads)
                if (thread.IsAlive) return true;
            return false;
        }
    }

    /// <summary>
    /// Binds the camera and starts the LodGen pool. Refuses (with a warning) while a thread from
    /// an earlier pool is still alive, which only happens after a <see cref="Stop"/> timed out: a
    /// second pool then would double the workers and leave the straggler building regions from
    /// worldgen statics the caller believes are quiet.
    /// </summary>
    public void Start(Camera camera)
    {
        if (_isRunning) return;
        if (AnyThreadAlive)
        {
            Log.Warn("LodGen: Start refused, a worker from the previous pool is still running");
            return;
        }

        _owner.AttachCamera(camera);
        // A timed-out Stop leaves the event set; a new pool must start with it clear or every
        // idle worker would spin instead of waiting.
        _wake.Reset();
        _isRunning = true;

        var threads = new Thread[StreamingConfig.MaxConcurrentLodGenerations];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"LodGen-{i}",
                Priority = ThreadPriority.BelowNormal
            };
        }

        // Published before any thread runs, so AnyThreadAlive can never miss a started worker.
        _threads = threads;
        foreach (var thread in threads)
            thread.Start();

        // Managed thread names are not observable from Process.GetCurrentProcess().Threads, so
        // this line is the only way to confirm the pool size a running build actually spawned.
        Log.WriteLine($"LodGen: {StreamingConfig.MaxConcurrentLodGenerations} workers");
    }

    /// <summary>
    /// Stops the LodGen workers and joins them, each for at most
    /// <see cref="SaveConfig.WorkerStopJoinTimeoutMs"/>. A worker mid-build finishes
    /// (and enqueues) that region first; one waiting out a back-off is woken.
    /// <para>
    /// True means every LodGen thread has exited, so none can read the heightmap cache or the
    /// worldgen statics any more: part of the precondition <see cref="WorldSeed.Apply"/> needs,
    /// together with <see cref="ChunkGenerationWorker.Stop"/> and
    /// <see cref="ChunkManager.DrainMeshWork"/>. False means a thread outlived its join; the pool
    /// is kept so a later Stop can re-join it, and <see cref="WorldSeed.Apply"/> must NOT run.
    /// Calling it when <see cref="Start"/> never ran returns true.
    /// </para>
    /// </summary>
    public bool Stop()
    {
        _isRunning = false;
        _wake.Set();

        var threads = _threads;
        if (threads == null) return true;

        var allJoined = true;
        foreach (var thread in threads)
        {
            if (thread.Join(SaveConfig.WorkerStopJoinTimeoutMs)) continue;
            allJoined = false;
            Log.Warn($"LodGen: {thread.Name} did not stop within {SaveConfig.WorkerStopJoinTimeoutMs} ms");
        }

        if (!allJoined) return false;

        _threads = null;
        _wake.Reset();
        return true;
    }

    private void WorkerLoop()
    {
        while (_isRunning)
        {
            try
            {
                switch (_claims.TryBuildOneRegion())
                {
                    // Nothing left to build anywhere: the long poll, exactly as before.
                    case LodBuildOutcome.Idle:
                        _wake.Wait(StreamingConfig.GenerationDelayIdleMs);
                        break;
                    // Work exists and only the main thread's drain is behind, so backing off for
                    // the idle poll would stall the streaming burst a fifth of a second at a time.
                    case LodBuildOutcome.Throttled:
                        _wake.Wait(StreamingConfig.LodBuildThrottleDelayMs);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"Error in LOD generation: {ex}");
                _wake.Wait(1000);
            }
        }
    }
}
