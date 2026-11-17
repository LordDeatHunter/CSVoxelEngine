using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Core;

public sealed partial class WorldLifecycleTests
{
    [Fact]
    public void NullSavedMobIsSkippedBeforeLoadingCompletes()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        WriteRawEntitiesFile($$"""{"Mobs":[{"Type":"slime","Position":[3,{{High}},3],"Health":1},null]}""");
        var world = NewWorld(new ChunkSaveStore(_dir));
        var loaded = false;
        var timedOut = false;
        var restoredMobs = 0;
        var closed = false;
        Exception? updateFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            updateFailure = Record.Exception(() => loaded = RunUntil(world, () => world.IsLoaded, LoadWallSeconds));
            timedOut = world.LoadTimedOut;
            restoredMobs = world.Slimes.Count;
        }
        finally
        {
            cleanupFailure = Record.Exception(() => closed = world.SaveAndDispose());
        }

        Assert.True(closed && cleanupFailure == null,
            $"ordinary cleanup failed: {cleanupFailure}; update failure: {updateFailure}");
        Assert.True(updateFailure == null, $"Loading-to-Playing update failed: {updateFailure}");
        Assert.True(loaded, "the world did not finish loading");
        Assert.False(timedOut, "the world reached its loading timeout instead of readiness");
        Assert.Equal(1, restoredMobs);
    }

    [Fact]
    public void NullSavedDropIsSkippedBeforeLoadingCompletes()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        WriteRawEntitiesFile(DropsWithNull());
        var world = NewWorld(new ChunkSaveStore(_dir));
        var loaded = false;
        var timedOut = false;
        var restoredDrops = 0;
        var closed = false;
        Exception? updateFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            updateFailure = Record.Exception(() => loaded = RunUntil(world, () => world.IsLoaded, LoadWallSeconds));
            timedOut = world.LoadTimedOut;
            restoredDrops = world.Drops.Count;
        }
        finally
        {
            cleanupFailure = Record.Exception(() => closed = world.SaveAndDispose());
        }

        Assert.True(closed && cleanupFailure == null,
            $"ordinary cleanup failed: {cleanupFailure}; update failure: {updateFailure}");
        Assert.True(updateFailure == null, $"Loading-to-Playing update failed: {updateFailure}");
        Assert.True(loaded, "the world did not finish loading");
        Assert.False(timedOut, "the world reached its loading timeout instead of readiness");
        Assert.Single(world.Drops);
    }

    [Fact]
    public void LoadingCloseResavesOnlyNonNullEntityRecords()
    {
        WritePlayerFile(new PlayerSaveData { Position = [0f, High, 0f] });
        var rockName = JsonSerializer.Serialize(SaveNames.NameOf(TerrainGenerator.RockBlock));
        WriteRawEntitiesFile($$"""{"Mobs":[{"Type":"slime","Position":[3,{{High}},3],"Health":1},null],"Drops":[{"Item":{{rockName}},"Position":[1,{{High}},1]},null]}""");

        var world = NewWorld(new ChunkSaveStore(_dir));
        Assert.Equal(WorldPhase.Loading, world.Phase);
        Assert.True(world.SaveAndDispose());

        var store = new ChunkSaveStore(_dir);
        var saved = EntitySaveCodec.TryParse(store.ReadFile(EntitySaveCodec.FileName), out var rejectedJson);
        var shutdown = store.Shutdown(TimeSpan.FromSeconds(10));

        Assert.True(shutdown);
        Assert.False(rejectedJson);
        Assert.NotNull(saved);
        Assert.Equal("slime", Assert.Single(saved!.Mobs!).Type);
        Assert.Equal(SaveNames.NameOf(TerrainGenerator.RockBlock), Assert.Single(saved.Drops!).Item);
        Assert.False(File.Exists(Path.Combine(_dir, EntitySaveCodec.FileName + ".bad")));
    }

    private void WriteRawEntitiesFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, EntitySaveCodec.FileName), Encoding.UTF8.GetBytes(json));
    }

    private string DropsWithNull()
    {
        var rockName = JsonSerializer.Serialize(SaveNames.NameOf(TerrainGenerator.RockBlock));
        return $$"""{"Drops":[{"Item":{{rockName}},"Position":[1,{{High}},1]},null]}""";
    }
}
