using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class SlimeTests
{
    public SlimeTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static SlimeManager Manager() => new(EntityRegistry.Require("slime"));

    private static ChunkManager FloorWorld(byte topId)
    {
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        chunks.Chunks[Vector3.Zero] = chunk;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, topId, 0);
        return chunks;
    }

    private static Vector3 GroundBody(float x, float z) => new(x, 1f + Slime.HalfExtent, z);

    [Fact]
    public void GroundedSlimeIsHarmless()
    {
        var chunks = FloorWorld(ContentRegistry.RequireBlock("stone").Id);
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
        var chunks = FloorWorld(ContentRegistry.RequireBlock("stone").Id);
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
        var chunks = FloorWorld(ContentRegistry.RequireBlock("stone").Id);
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
        var chunks = FloorWorld(ContentRegistry.RequireBlock("stone").Id);
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
        var chunks = FloorWorld(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(100f, 1.62f, 100f), yaw: 0f, pitch: 0f);

        var slime = manager.Spawn(GroundBody(8.5f, 8.5f));
        slime.TakeDamage(1000);
        manager.Update(Dt, chunks, drops, camera);

        Assert.Empty(manager.Slimes);
        var slimeball = ContentRegistry.Require("item/slimeball");
        Assert.Contains(drops.Drops, drop => drop.BlockId == slimeball);
    }

    [Fact]
    public void SpawnRingPlacesSlimesOnDryLoadedSurface()
    {
        var chunks = SurfaceWorld();
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
    public void LivingCapStopsFurtherSpawns()
    {
        var chunks = SurfaceWorld();
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 100f, 0.5f), yaw: 0f, pitch: 0f);

        for (var i = 0; i < WorldGenerationConfig.SlimeMaxAlive; i++)
            manager.Spawn(new Vector3(0.5f, 1f + Slime.HalfExtent, 0.5f));

        manager.Update(WorldGenerationConfig.SlimeSpawnIntervalSeconds + 0.01f, chunks, drops, camera);

        Assert.Equal(WorldGenerationConfig.SlimeMaxAlive, manager.Slimes.Count);
    }

    [Fact]
    public void SlimesOutsideTheDespawnRadiusAreCulled()
    {
        var chunks = FloorWorld(ContentRegistry.RequireBlock("stone").Id);
        var drops = new DropManager();
        var manager = Manager();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: 0f, pitch: 0f);

        manager.Spawn(new Vector3(
            WorldGenerationConfig.SlimeDespawnRadiusBlocks + 4f, 1f + Slime.HalfExtent, 0.5f));
        manager.Update(Dt, chunks, drops, camera);

        Assert.Empty(manager.Slimes);
    }

    // Builds the surface layer of a 7x7 chunk area at each column's real worldgen height, so the
    // spawn ring finds solid loaded ground and two clear cells above every dry column.
    private static ChunkManager SurfaceWorld()
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var cx = -3; cx <= 3; cx++)
        for (var cz = -3; cz <= 3; cz++)
        {
            var heights = Chunk.GetColumnHeights(cx, cz);
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var height = heights[(lx << Chunk.SizeShift) | lz];
                var cy = height >> Chunk.SizeShift;
                var pos = new Vector3(cx, cy, cz);
                if (!chunks.Chunks.TryGetValue(pos, out var chunk))
                {
                    chunk = new Chunk(null!, pos, generateBlockData: false);
                    chunk.IsBlockDataReady = true;
                    chunks.Chunks[pos] = chunk;
                }
                chunk.SetBlockRaw(lx, height & Chunk.SizeMask, lz, stone, 0);
            }
        }
        return chunks;
    }
}
