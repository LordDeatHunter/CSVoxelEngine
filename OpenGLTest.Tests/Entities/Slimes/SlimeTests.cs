using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Entities.Slimes;

public sealed class SlimeTests
{
    public SlimeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static SlimeManager Manager() => new(EntityRegistry.Require("slime"));

    private static Vector3 GroundBody(float x, float z) => new(x, 1f + Slime.HalfExtent, z);

    [Fact]
    public void GroundedSlimeIsHarmless()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = new Slime(EntityRegistry.Require("slime"), GroundBody(2.5f, 2.5f));

        // The player stands on the slime's own column, so it never hops and stays grounded.
        for (var i = 0; i < 180; i++)
            slime.Update(Dt, chunks, new Vector3(2.5f, 1.62f, 2.5f));

        Assert.True(slime.IsGrounded);
        Assert.False(slime.CanDealContactDamage);
    }

    [Fact]
    public void AirborneSlimeDealsDamageUntilItsCooldown()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = new Slime(EntityRegistry.Require("slime"), GroundBody(2.5f, 2.5f));
        var player = new Vector3(5.5f, 1.62f, 2.5f);

        for (var i = 0; i < 600 && slime.IsGrounded; i++)
            slime.Update(Dt, chunks, player);

        Assert.True(slime.IsAirborne);
        Assert.True(slime.CanDealContactDamage);

        slime.NotifyContactDamage();
        Assert.False(slime.CanDealContactDamage);
    }

    [Fact]
    public void HopCarriesTheSlimeTowardThePlayer()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = new Slime(EntityRegistry.Require("slime"), GroundBody(2.5f, 2.5f));
        var player = new Vector3(2.5f + 6f, 1.62f, 2.5f);
        var startX = slime.Position.X;

        for (var i = 0; i < 600 && slime.Position.X < startX + 0.1f; i++)
            slime.Update(Dt, chunks, player);

        Assert.True(slime.Position.X > startX + 0.1f);
    }

    [Fact]
    public void GroundedContactShovesTheSlimeAwayFromThePlayer()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var manager = Manager();
        var slime = manager.Spawn(GroundBody(8.5f, 8.5f));
        // Only a few frames so the hop timer cannot reach its next jump during the window.
        for (var i = 0; i < 10; i++)
            slime.Update(Dt, chunks, new Vector3(8.5f, 1.62f, 8.5f));
        Assert.True(slime.IsGrounded);

        // Player overlaps the slime's body from the -X side, so the shove must push it +X.
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8.1f, 1.62f, 8.5f), yaw: 0f, pitch: 0f);
        var before = slime.Position.X;
        for (var i = 0; i < 8; i++)
            manager.Update(Dt, chunks, drops, camera);

        Assert.True(slime.Position.X > before + 0.02f);
        Assert.Equal(0, manager.DamageDealtThisFrame);
    }

    [Fact]
    public void KillingASlimeDropsSlimeballs()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(100f, 1.62f, 100f), yaw: 0f, pitch: 0f);

        var slime = manager.Spawn(GroundBody(8.5f, 8.5f));
        slime.TakeDamage(1000);
        manager.Update(Dt, chunks, drops, camera);

        // The loot drops at once; the corpse plays its pop and is then removed.
        Assert.True(slime.IsDying);
        Assert.Equal(0, manager.LivingCount);
        manager.Update(slime.DeathSeconds + 0.1f, chunks, drops, camera);
        Assert.Empty(manager.Slimes);
        var slimeball = ContentRegistry.Require("item/slimeball");
        Assert.Contains(drops.Drops, drop => drop.BlockId == slimeball);
    }

    [Fact]
    public void TakeDamageSnapsTheHitFlashWhichThenFades()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var slime = new Slime(EntityRegistry.Require("slime"), GroundBody(2.5f, 2.5f));
        var player = new Vector3(2.5f, 1.62f, 2.5f);

        Assert.Equal(0f, slime.HitFlash);
        slime.TakeDamage(1);
        Assert.Equal(1f, slime.HitFlash);

        slime.Update(Dt, chunks, player);
        Assert.InRange(slime.HitFlash, 0f, 1f);

        for (var i = 0; i < 60; i++)
            slime.Update(Dt, chunks, player);
        Assert.Equal(0f, slime.HitFlash);
    }

    [Fact]
    public void SpawnRingPlacesSlimesOnDryLoadedSurface()
    {
        var chunks = MobTestWorld.Surface();
        var player = new Vector3(0.5f, 100f, 0.5f);

        var found = false;
        var position = Vector3.Zero;
        for (var attempt = 0; attempt < 40 && !found; attempt++)
            found = SlimeManager.TryFindSpawn(chunks, player, out position);

        Assert.True(found);
        var dx = position.X - player.X;
        var dz = position.Z - player.Z;
        Assert.InRange(MathF.Sqrt(dx * dx + dz * dz), 18f, 42f);
        var groundY = (int)MathF.Floor(position.Y - 2f * Slime.HalfExtent);
        Assert.True(BlockType.IsSolid(
            chunks.GetBlockGlobal((int)MathF.Floor(position.X), groundY, (int)MathF.Floor(position.Z))));
    }

    [Fact]
    public void NaturalCapStopsFurtherRingSpawns()
    {
        var chunks = MobTestWorld.Surface();
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 100f, 0.5f), yaw: 0f, pitch: 0f);

        for (var i = 0; i < WorldGenerationConfig.MobNaturalCap; i++)
            manager.Spawn(new Vector3(0.5f, 1f + Slime.HalfExtent, 0.5f));

        manager.Update(WorldGenerationConfig.SlimeSpawnIntervalSeconds + 0.01f, chunks, drops, camera);

        Assert.Equal(WorldGenerationConfig.MobNaturalCap, manager.Slimes.Count);
    }

    [Fact]
    public void SlimesOutsideTheDespawnRadiusFadeOutWithoutLoot()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: 0f, pitch: 0f);

        var stray = manager.Spawn(new Vector3(
            WorldGenerationConfig.SlimeDespawnRadiusBlocks + 4f, 1f + Slime.HalfExtent, 0.5f))!;
        manager.Update(Dt, chunks, drops, camera);

        // It plays the death pose instead of vanishing, no longer counts, and drops nothing
        Assert.True(stray.IsDying);
        Assert.Equal(0, manager.LivingCount);
        manager.Update(stray.DeathSeconds + 0.1f, chunks, drops, camera);
        Assert.Empty(manager.Slimes);
        Assert.Empty(drops.Drops);
    }

    [Fact]
    public void KnockbackPushesThePlayerAndThenDecays()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        var keys = new ScriptedKeyboard();

        camera.AddKnockback(new Vector3(1f, 0f, 0f), 6f, 0f);
        var startX = camera.Position.X;
        for (var i = 0; i < 6; i++)
            camera.Update(Dt, keys, chunks);
        Assert.True(camera.Position.X > startX + 0.1f);

        for (var i = 0; i < 60; i++)
            camera.Update(Dt, keys, chunks);
        var settledX = camera.Position.X;
        for (var i = 0; i < 6; i++)
            camera.Update(Dt, keys, chunks);
        Assert.InRange(MathF.Abs(camera.Position.X - settledX), 0f, 1e-3f);
    }

    [Fact]
    public void KnockbackCannotPushThePlayerThroughAWall()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        for (var y = 1; y <= 2; y++)
            chunks.Chunks[Vector3.Zero].SetBlockRaw(4, y, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(3.2f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        var keys = new ScriptedKeyboard();

        camera.AddKnockback(new Vector3(1f, 0f, 0f), 12f, 0f);
        for (var i = 0; i < 30; i++)
            camera.Update(Dt, keys, chunks);

        Assert.True(camera.Position.X < 3.8f, $"slid into the wall: {camera.Position.X}");
    }

    [Fact]
    public void AirborneSlimeHitKnocksThePlayerBack()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(3.0f, 1f + Camera.EyeHeight, 2.5f), yaw: 0f, pitch: 0f);
        manager.Spawn(new Vector3(2.5f, 1f + Slime.HalfExtent, 2.5f));
        var keys = new ScriptedKeyboard();

        var damaged = false;
        for (var i = 0; i < 300 && !damaged; i++)
        {
            manager.Update(Dt, chunks, drops, camera);
            damaged = manager.DamageDealtThisFrame > 0;
        }
        Assert.True(damaged);
        Assert.Equal(WorldGenerationConfig.SlimeContactDamage, manager.DamageDealtThisFrame);

        // The slime sits to the -X side, so the knockback must carry the player further +X.
        var beforeX = camera.Position.X;
        for (var i = 0; i < 8; i++)
            camera.Update(Dt, keys, chunks);
        Assert.True(camera.Position.X > beforeX + 0.02f);
    }

    [Fact]
    public void SlimeFloatsUpToTheWaterSurface()
    {
        var chunks = MobTestWorld.Pool(waterCells: 5);
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(8.5f, 1f + Slime.HalfExtent, 8.5f));
        var player = new Vector3(8.5f, 6.62f, 8.5f);

        for (var i = 0; i < 600; i++)
            slime.Update(Dt, chunks, player);

        // The top source (y = 5) surfaces at 5 + 8/9, so a buoyant slime rises to bob there
        // instead of sinking to the floor it spawned on.
        var feet = slime.Position.Y - Slime.HalfExtent;
        Assert.InRange(feet, 5.5f, 6.3f);
        Assert.False(slime.IsGrounded);
    }

    [Fact]
    public void WaterHopIsFlatterAndFasterAtTheSameTotalSpeed()
    {
        var land = Slime.HopVelocity(Vector3.UnitX, inWater: false);
        var water = Slime.HopVelocity(Vector3.UnitX, inWater: true);

        // Same total speed, half the lift, and the released vertical moved into the horizontal.
        Assert.Equal(land.Length(), water.Length(), 4);
        Assert.Equal(land.Y / 2f, water.Y, 4);
        Assert.True(water.X > land.X * 1.9f, $"water horizontal {water.X} vs land {land.X}");
    }
}
