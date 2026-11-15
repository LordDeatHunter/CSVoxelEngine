using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// A ground mob standing flush against a one-block step climbs it: the auto-jump (walkers) and the
/// hop (slimes) keep their sideways push while rising instead of rising and falling in one column.
/// </summary>
public sealed class StepUpTests
{
    public StepUpTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const int WallX = 6;

    private static ChunkManager FloorWithStep()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        // One block high (y = 1 sits on the y = 0 floor), spanning the whole row
        for (var x = WallX; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunks.Chunks[Vector3.Zero].SetBlockRaw(x, 1, z, stone, 0);
        return chunks;
    }

    [Fact]
    public void WalkerFlushAgainstAOneBlockStepClimbsIt()
    {
        var chunks = FloorWithStep();
        var goblin = new Goblin(EntityRegistry.Require("goblin"),
            new Vector3(WallX - 0.45f, 1f + Goblin.Height * 0.5f, 8.5f), ammo: 0);
        var player = new Camera(null, null);
        player.TestTeleport(new Vector3(WallX + 6f, 2f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);

        for (var i = 0; i < (int)(4f / Dt) && goblin.Position.X < WallX + 0.5f; i++)
            goblin.Update(Dt, chunks, player);

        Assert.True(goblin.Position.X >= WallX + 0.5f, $"stuck against the step at x = {goblin.Position.X}");
    }

    [Fact]
    public void SlimeFlushAgainstAOneBlockStepHopsOntoIt()
    {
        var chunks = FloorWithStep();
        var type = EntityRegistry.Require("slime");
        var half = Slime.HalfExtentFor(SlimeSize.Min);
        var slime = new Slime(type, new Vector3(WallX - half - 0.01f, 1.05f + half, 8.5f));
        var hops = 0;
        slime.Hopped += () => hops++;
        var target = new Vector3(WallX + 6f, 2f, 8.5f);

        for (var i = 0; i < (int)(8f / Dt) && slime.Position.X < WallX + 0.3f; i++)
            slime.Update(Dt, chunks, target);

        Assert.True(slime.Position.X >= WallX + 0.3f, $"stuck against the step at x = {slime.Position.X}, y = {slime.Position.Y}, hops = {hops}, half = {half}");
    }
}
