using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class GoblinManagerTests
{
    public GoblinManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float Z = 8.5f;

    private static GoblinManager Manager() => new(EntityRegistry.Require("goblin"));

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // A goblin body centre standing on the stone floor (feet at y = 1).
    private static Vector3 GroundBody(float x, float z) => new(x, 1f + Goblin.Height * 0.5f, z);

    // A player standing on the stone floor at (x, z).
    private static Camera StandingPlayer(float x, float z)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(x, 1f + Camera.EyeHeight, z), yaw: 0f, pitch: 0f);
        return camera;
    }

    private static int CountFlint(DropManager drops)
    {
        var flint = ContentRegistry.RequireItem("flint").Id;
        return drops.Drops.Count(d => d.BlockId == flint);
    }

    [Fact]
    public void ThrownStoneReachesAStandingPlayer()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = Manager();
        var camera = StandingPlayer(9.5f, Z);
        manager.Spawn(GroundBody(2.5f, Z), ammo: 3);
        var chest = Goblin.ChestOf(camera.Position);

        var sawStone = false;
        var hit = false;
        var closest = float.MaxValue;
        for (var i = 0; i < (int)(15f / Dt) && !hit; i++)
        {
            manager.Update(Dt, chunks, drops, camera);
            hit = manager.DamageDealtThisFrame > 0;
            foreach (var stone in manager.Stones)
            {
                sawStone = true;
                closest = MathF.Min(closest, Vector3.Distance(stone.Position, chest));
            }
        }

        Assert.True(sawStone || hit);
        Assert.True(hit || closest <= 0.5f, $"no hit; closest pass {closest:F2} blocks from the chest");
    }

    [Fact]
    public void FlyingPlayerIsImmuneToStonesAndSwings()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = Manager();
        var camera = StandingPlayer(9.5f, Z);
        camera.TestSetFlight(true);
        manager.Spawn(GroundBody(2.5f, Z), ammo: 3);
        var brawler = manager.Spawn(GroundBody(9.5f, Z + 1.5f), ammo: 0);

        var sawStone = false;
        var sawSwing = false;
        for (var i = 0; i < (int)(15f / Dt); i++)
        {
            manager.Update(Dt, chunks, drops, camera);
            Assert.Equal(0, manager.DamageDealtThisFrame);
            sawStone |= manager.Stones.Count > 0;
            sawSwing |= brawler.Animation.IsPlaying(Goblin.MeleeClip);
        }

        Assert.True(sawStone);
        Assert.True(sawSwing);
    }

    [Fact]
    public void MeleeDamageRepeatsOnlyAfterTheCooldown()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = Manager();
        var camera = StandingPlayer(9.5f, Z);
        manager.Spawn(GroundBody(7.5f, Z), ammo: 0);

        var damageFrames = new System.Collections.Generic.List<int>();
        for (var i = 0; i < (int)(10f / Dt) && damageFrames.Count < 2; i++)
        {
            manager.Update(Dt, chunks, drops, camera);
            if (manager.DamageDealtThisFrame > 0) damageFrames.Add(i);
        }

        Assert.Equal(2, damageFrames.Count);
        Assert.True(damageFrames[1] - damageFrames[0]
                    >= (int)(WorldGenerationConfig.GoblinMeleeCooldownSeconds / Dt) - 1);
    }

    [Fact]
    public void DeathDropsTheRemainingStones()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = Manager();
        var camera = StandingPlayer(9.5f, Z);
        const int ammo = 4;
        var goblin = manager.Spawn(GroundBody(2.5f, Z), ammo);

        goblin.TakeDamage(goblin.Type.MaxHealth);
        manager.Update(Dt, chunks, drops, camera);

        // Loot drops at once; the corpse stays drawn until the death has played out.
        Assert.True(goblin.IsDying);
        Assert.Equal(0, manager.LivingCount);
        manager.Update(goblin.DeathSeconds + 0.1f, chunks, drops, camera);
        Assert.Empty(manager.Goblins);
        Assert.True(CountFlint(drops) >= ammo);
    }

    [Fact]
    public void MissedStonesShatterWithoutADrop()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = Manager();
        var camera = StandingPlayer(9.5f, Z);
        var goblin = manager.Spawn(GroundBody(2.5f, Z), ammo: 3);

        // Let the goblin see the player and start its throw, so the line-of-sight gate passed.
        var frames = 0;
        for (; frames < (int)(10f / Dt) && !goblin.Animation.IsPlaying(Goblin.ThrowClip); frames++)
            manager.Update(Dt, chunks, drops, camera);
        Assert.True(goblin.Animation.IsPlaying(Goblin.ThrowClip));

        // Then wall the player off before the stone leaves the hand.
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y <= 5; y++)
            chunk.SetBlockRaw(6, y, z, stone, 0);

        var sawStone = false;
        for (var i = 0; i < (int)(10f / Dt); i++)
        {
            manager.Update(Dt, chunks, drops, camera);
            Assert.Equal(0, manager.DamageDealtThisFrame);
            if (manager.Stones.Count > 0) sawStone = true;
            else if (sawStone) break;
        }

        Assert.True(sawStone);
        Assert.Empty(manager.Stones);
        Assert.Empty(drops.Drops);
    }

    [Fact]
    public void LivingCapAndDespawnRadiusApply()
    {
        var drops = new DropManager();
        var interval = WorldGenerationConfig.GoblinSpawnIntervalSeconds + 0.01f;

        // Below the cap the spawn ring places a goblin on dry ground around the player.
        var surface = MobTestWorld.Surface();
        var high = new Camera(null, null);
        high.TestTeleport(new Vector3(0.5f, 100f, 0.5f), yaw: 0f, pitch: 0f);
        var spawning = Manager();
        for (var attempt = 0; attempt < 40 && spawning.Goblins.Count == 0; attempt++)
            spawning.Update(interval, surface, drops, high);
        Assert.Single(spawning.Goblins);
        var spawned = spawning.Goblins[0].Position;
        var ring = MathF.Sqrt((spawned.X - 0.5f) * (spawned.X - 0.5f) + (spawned.Z - 0.5f) * (spawned.Z - 0.5f));
        Assert.InRange(ring, WorldGenerationConfig.GoblinSpawnMinRadiusBlocks - 2f,
            WorldGenerationConfig.GoblinSpawnMaxRadiusBlocks + 2f);
        var feetCell = (int)MathF.Floor(spawned.Y - Goblin.Height * 0.5f) - 1;
        Assert.True(BlockType.IsSolid(surface.GetBlockGlobal(
            (int)MathF.Floor(spawned.X), feetCell, (int)MathF.Floor(spawned.Z))));

        // At the natural cap the ring spawns nothing more.
        var capped = Manager();
        for (var i = 0; i < WorldGenerationConfig.MobNaturalCap; i++)
            capped.Spawn(new Vector3(0.5f, 1f + Goblin.Height * 0.5f, 0.5f), ammo: 0);
        capped.Update(interval, surface, drops, high);
        Assert.Equal(WorldGenerationConfig.MobNaturalCap, capped.Goblins.Count);

        // Beyond the despawn radius a goblin is culled, inside it stays.
        var floor = StoneFloor();
        var camera = StandingPlayer(0.5f, 0.5f);
        var culling = Manager();
        var far = culling.Spawn(GroundBody(WorldGenerationConfig.GoblinDespawnRadiusBlocks + 4f, 0.5f), ammo: 0);
        var near = culling.Spawn(GroundBody(WorldGenerationConfig.GoblinDespawnRadiusBlocks - 4f, 0.5f), ammo: 0);
        culling.Update(Dt, floor, drops, camera);
        Assert.DoesNotContain(far, culling.Goblins);
        Assert.Contains(near, culling.Goblins);
    }
}
