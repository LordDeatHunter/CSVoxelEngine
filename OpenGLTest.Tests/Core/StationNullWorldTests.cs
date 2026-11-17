using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Lanterns;
using Xunit;

namespace VoxelEngine.Tests.Core;

/// <summary>A real save-backed world keeps valid station siblings around null records.</summary>
[Collection("Worldgen seed")]
public sealed class StationNullWorldTests
{
    private const float High = 1e6f;

    [Fact]
    public void StoreBackedWorldSkipsNullStationRowsAndResavesValidSiblings()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var initialStore = new ChunkSaveStore(directory);
        var initialStoreClosed = false;
        World? world = null;
        try
        {
            initialStore.EnqueueFile(PlayerSaveCodec.FileName,
                PlayerSaveCodec.ToBytes(new PlayerSaveData { Position = [0f, High, 0f] }));
            initialStore.EnqueueFile(StationSaveCodec.FileName, StationSaveCodec.ToBytes(new StationSaveData
            {
                Campfires =
                [
                    null!,
                    new StationSaveData.CampfireData { Cell = [2, (int)High, 2], Lit = true, BurnLeft = 12f },
                ],
                Lanterns =
                [
                    null!,
                    new LanternData { Cell = [4, (int)High, 4], Block = "block/lantern", Remaining = 15f, Capacity = 60f },
                ],
            }));
            initialStoreClosed = initialStore.Shutdown(TimeSpan.FromSeconds(10));
            Assert.True(initialStoreClosed, "the initial test save did not drain");

            world = new World(null!, null!, null!, null!, null!, null!, null,
                new ChunkSaveStore(directory));
            Assert.True(RunUntil(world, () => world.IsPlaying, 60), "the store-backed world did not become ready");
            Assert.False(world.LoadTimedOut);
            Assert.True(world.Campfires.IsLit(2, (int)High, 2));
            Assert.Equal(15f, LanternState.Of(world.Lanterns.Peek((4, (int)High, 4)))!.Remaining);
            Assert.True(world.SaveAndDispose());

            var saved = StationSaveCodec.TryParse(File.ReadAllBytes(Path.Combine(directory, StationSaveCodec.FileName)));
            Assert.NotNull(saved);
            Assert.Equal(12f, Assert.Single(saved!.Campfires!).BurnLeft);
            var lantern = Assert.Single(saved.Lanterns!);
            Assert.Equal(15f, lantern.Remaining);
            Assert.Equal(new[] { 4, (int)High, 4 }, lantern.Cell);
        }
        finally
        {
            if (!initialStoreClosed)
                initialStore.Shutdown(TimeSpan.FromSeconds(10));
            world?.SaveAndDispose();
            try
            {
                WorldSeed.Apply(WorldSeed.Default);
            }
            catch (InvalidOperationException)
            {
                // A timed-out worker keeps the world seed locked; the assertion already reports the failure.
            }
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool RunUntil(World world, Func<bool> done, double wallSeconds)
    {
        var stopwatch = Stopwatch.StartNew();
        var last = 0d;
        while (!done())
        {
            if (stopwatch.Elapsed.TotalSeconds > wallSeconds)
                return false;
            var now = stopwatch.Elapsed.TotalSeconds;
            world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }
        return true;
    }
}
