using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Goblin pack spawning: a ring spawn rolls one to
/// <see cref="WorldGenerationConfig.GoblinPackMaxSize"/> goblins together, the rest of the pack
/// landing within <see cref="WorldGenerationConfig.GoblinPackSpawnRadiusBlocks"/> of the found
/// cell, and every member respects the shared natural cap.
/// </summary>
public sealed class GoblinPackSpawnTests
{
    public GoblinPackSpawnTests() => TestContent.EnsureLoaded();

    private static Camera Player()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 100f + Camera.EyeHeight, 0.5f), yaw: 0f, pitch: 0f);
        return camera;
    }

    [Fact]
    public void ARingSpawnRollsAPackOfOneToThreeTogether()
    {
        var chunks = MobTestWorld.Surface();
        var camera = Player();
        var goblins = new GoblinManager(EntityRegistry.Require("goblin"));
        var drops = new DropManager();

        var cycles = 0;
        var sawPack = false;
        for (var cycle = 0; cycle < 40 && goblins.LivingCount < WorldGenerationConfig.MobNaturalCap; cycle++)
        {
            var before = goblins.Goblins.ToArray();
            goblins.Update(WorldGenerationConfig.GoblinSpawnIntervalSeconds + 0.01f, chunks, drops, camera);
            var fresh = goblins.Goblins.Where(g => !before.Contains(g)).ToList();
            if (fresh.Count == 0) continue;
            cycles++;

            Assert.InRange(fresh.Count, 1, WorldGenerationConfig.GoblinPackMaxSize);
            var leader = fresh[0].Position;
            foreach (var member in fresh)
            {
                var dx = member.Position.X - leader.X;
                var dz = member.Position.Z - leader.Z;
                var limit = WorldGenerationConfig.GoblinPackSpawnRadiusBlocks + 1f;
                Assert.True(dx * dx + dz * dz <= limit * limit,
                    $"a pack member spawned {MathF.Sqrt(dx * dx + dz * dz):0.0} blocks from the leader");
            }

            if (fresh.Count > 1) sawPack = true;
        }

        Assert.True(cycles > 0, "no spawn cycle produced a goblin");
        Assert.True(sawPack, "no cycle ever rolled a pack of more than one");
        Assert.True(goblins.LivingCount <= WorldGenerationConfig.MobNaturalCap,
            $"the pack spawns passed the natural cap: {goblins.LivingCount}");
    }
}
