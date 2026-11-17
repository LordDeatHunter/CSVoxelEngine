using System;
using System.IO;
using System.Text;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Core;

/// <summary>Exercises pending entity close through a real store-backed, headless Loading world.</summary>
[Collection("Worldgen seed")]
public sealed class EntitySaveNumericLifecycleTests
{
    private const float High = 1e6f;

    public EntitySaveNumericLifecycleTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ClosingPendingMobWithNonFiniteFacingCompletesAndKeepsFiniteSiblings()
    {
        const string entities = """
            { "Mobs": [{ "Type": "slime", "Position": [3, 1000000, 3], "Facing": 1e1000, "Velocity": [0.25, 0, -0.5], "Health": 4 }], "Drops": [] }
            """;
        ClosePendingEntitiesAndCheck(entities, "mob");
    }

    [Fact]
    public void ClosingPendingDropWithNonFiniteAgeCompletesAndKeepsFiniteSiblings()
    {
        var rock = SaveNames.NameOf(TerrainGenerator.RockBlock);
        var entities = $$"""
            { "Mobs": [], "Drops": [{ "Item": "{{rock}}", "Position": [1, 1000000, 1], "Velocity": [0.25, 0, -0.5], "Age": -1e1000 }] }
            """;
        ClosePendingEntitiesAndCheck(entities, "drop");
    }

    private static void ClosePendingEntitiesAndCheck(string entitiesJson, string expectedRecord)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        WorldSeed.Apply(WorldSeed.Default);

        var setupStore = new ChunkSaveStore(directory);
        setupStore.EnqueueFile(PlayerSaveCodec.FileName,
            PlayerSaveCodec.ToBytes(new PlayerSaveData { Position = [0f, High, 0f] }));
        Assert.True(setupStore.Shutdown(TimeSpan.FromSeconds(10)));
        File.WriteAllBytes(Path.Combine(directory, EntitySaveCodec.FileName), Encoding.UTF8.GetBytes(entitiesJson));

        var store = new ChunkSaveStore(directory);
        var world = new World(null!, null!, null!, null!, null!, null!, null, store);
        Assert.Equal(WorldPhase.Loading, world.Phase);
        Assert.Empty(world.Mobs);
        Assert.Empty(world.Drops);

        // Each baseline red invocation runs as its own filtered test host. If serialization throws,
        // don't retry the same broken close in fixture cleanup; process exit owns abandoned workers,
        // writer and seed state before any other test host starts.
        var closeFailure = Record.Exception(() => world.SaveAndDispose());
        Assert.True(closeFailure == null, closeFailure?.ToString());

        Assert.Equal(WorldPhase.Disposed, world.Phase);
        Assert.False(WorldSeed.WorldIsLive);

        var reopened = new ChunkSaveStore(directory);
        var saved = EntitySaveCodec.TryParse(reopened.ReadFile(EntitySaveCodec.FileName));
        Assert.NotNull(saved);
        if (expectedRecord == "mob")
        {
            var mob = Assert.Single(saved!.Mobs!);
            Assert.Equal("slime", mob.Type);
            Assert.Equal(new[] { 3f, High, 3f }, mob.Position);
            Assert.Null(mob.Facing);
            Assert.Equal(new[] { 0.25f, 0f, -0.5f }, mob.Velocity);
        }
        else
        {
            var drop = Assert.Single(saved!.Drops!);
            Assert.Equal(SaveNames.NameOf(TerrainGenerator.RockBlock), drop.Item);
            Assert.Equal(new[] { 1f, High, 1f }, drop.Position);
            Assert.Equal(new[] { 0.25f, 0f, -0.5f }, drop.Velocity);
            Assert.Null(drop.Age);
        }

        Assert.True(reopened.Shutdown(TimeSpan.FromSeconds(10)));
        Directory.Delete(directory, recursive: true);
    }
}
