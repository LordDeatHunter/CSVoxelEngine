using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Sky;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// The sky's spawning (<see cref="MobSpawner.TryFindSkySpawn"/>, <see cref="SkyMobManager"/>): a ring spawn
/// only ever lands on an island top inside the sky band and inside an island zone, never on the cloud sea, in
/// the overworld or in hell, nor in the open sky between zones; the overworld searches never land on an island;
/// the ring fills up to the shared natural cap only around a target in the sky band, a galeraptor appearing in
/// the air over the island and no golem outside a big zone; a forced spawn passes the natural cap but not the
/// hard cap.
/// </summary>
public sealed class SkyMobManagerTests
{
    public SkyMobManagerTests() => TestContent.EnsureLoaded();

    private const int IslandTop = WorldGenerationConfig.SkyIslandBottomY + 36;
    private const int CloudSeaFloor = SkyIslandConfig.CloudSeaBottomY + 4;
    private const int OverworldFloor = 64;
    private const int HellFloor = WorldGenerationConfig.HellTopY - 40;

    // A flat stone slab whose top is `floor`, filling the one chunk layer that holds it over 7x7 chunks
    // around `center`, air above it inside that layer; nothing else is loaded
    private static ChunkManager Slab(Vector2 center, int floor)
    {
        var cy = floor >> Chunk.SizeShift;
        Assert.Equal(cy, (floor + 3) >> Chunk.SizeShift);
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var ccx = VoxelCoords.WorldToChunk(MathUtils.FloorToInt(center.X));
        var ccz = VoxelCoords.WorldToChunk(MathUtils.FloorToInt(center.Y));
        for (var cx = ccx - 3; cx <= ccx + 3; cx++)
        for (var cz = ccz - 3; cz <= ccz + 3; cz++)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, y, z, cy * Chunk.Size + y <= floor ? stone : (byte)0, 0);
            chunks.Chunks[pos] = chunk;
        }
        return chunks;
    }

    private static Vector3 EyeOn(Vector2 at, int floor) => new(at.X + 0.5f, floor + 1 + Camera.EyeHeight, at.Y + 0.5f);

    private static bool Search(ChunkManager chunks, Vector3 eye, out int x, out int groundY, out int z) =>
        MobSpawner.TryFindSkySpawn(chunks, eye, MobConfig.SkySpawnMinRadiusBlocks, MobConfig.SkySpawnMaxRadiusBlocks,
            64, MobConfig.SkySpawnSearchHalfHeight, MobConfig.SkySpawnScanDepth, out x, out groundY, out z);

    private static SkyMobManager NewManager(MobCaps? caps = null) =>
        new(EntityRegistry.Require(Galeraptor.EntityName), EntityRegistry.Require(Cloudgolem.EntityName), caps);

    [Fact]
    public void AnIslandTopInAZoneTakesASpawnAndNothingOutsideTheBandOrTheZonesDoes()
    {
        var zone = SkyZoneFinder.Center(_ => true);
        var island = Slab(zone, IslandTop);
        Assert.True(Search(island, EyeOn(zone, IslandTop), out var x, out var groundY, out var z));
        Assert.Equal(IslandTop, groundY);
        Assert.True(MobSpawner.InSkyBand(groundY) && MobSpawner.InSkyBand(groundY + 2));
        Assert.True(SkyIslands.ZoneAt(x + 0.5f, z + 0.5f).InZone);

        var open = SkyZoneFinder.OpenSky(MobConfig.SkySpawnMaxRadiusBlocks + 8f);
        for (var i = 0; i < 20; i++)
        {
            // The cloud sea under the islands, the overworld, hell and the open sky between zones
            Assert.False(Search(Slab(zone, CloudSeaFloor), EyeOn(zone, CloudSeaFloor), out _, out _, out _));
            Assert.False(Search(Slab(zone, OverworldFloor), EyeOn(zone, OverworldFloor), out _, out _, out _));
            Assert.False(Search(Slab(zone, HellFloor), EyeOn(zone, HellFloor), out _, out _, out _));
            Assert.False(Search(Slab(open, IslandTop), EyeOn(open, IslandTop), out _, out _, out _));
        }
    }

    [Fact]
    public void TheOverworldSearchesNeverLandOnAnIsland()
    {
        var zone = SkyZoneFinder.Center(_ => true);
        var island = Slab(zone, IslandTop);
        var eye = EyeOn(zone, IslandTop);
        for (var i = 0; i < 20; i++)
        {
            Assert.False(MobSpawner.TryFindSurfaceSpawn(island, eye, MobConfig.SlimeSpawnMinRadiusBlocks,
                MobConfig.SlimeSpawnMaxRadiusBlocks, 64, out _, out _, out _));
            Assert.False(MobSpawner.TryFindCaveSpawn(island, eye, CaveSlimeTests.AnyCave with { Tries = 64 }, null,
                out _, out _, out _, out _));
        }
    }

    [Fact]
    public void TheRingFillsAnIslandUpToTheNaturalCapOnlyAroundATargetInTheSkyBand()
    {
        var zone = SkyZoneFinder.Center(z => z.Tier < SkyZone.BigTier);
        var chunks = Slab(zone, IslandTop);
        var manager = NewManager(new MobCaps());
        var drops = new DropManager();
        IMobTarget[] targets = [new StubTarget(EyeOn(zone, IslandTop)) { IsFlying = true }];

        var seen = new HashSet<SkyMob>();
        for (var i = 0; i < 800; i++)
        {
            manager.Update(0.5f, chunks, drops, targets);
            Assert.True(manager.LivingCount <= MobConfig.MobNaturalCap, $"{manager.LivingCount} sky mobs passed the natural cap");
            // A mob added this update still stands where it spawned
            foreach (var mob in manager.Mobs)
            {
                if (!seen.Add(mob)) continue;
                Assert.IsType<Galeraptor>(mob); // no golem outside a big zone
                Assert.True(mob.Position.Y - IslandTop - 1 >= MobConfig.SkyFlyerSpawnLiftMin - 0.01f,
                    "a galeraptor appeared too low over the island");
            }
        }
        Assert.True(manager.LivingCount > 0, "nothing spawned on the islands");

        // A target on the ground under the islands is never spawned around
        var ground = NewManager();
        IMobTarget[] below = [new StubTarget(EyeOn(zone, OverworldFloor)) { IsFlying = true }];
        var overworld = Slab(zone, OverworldFloor);
        for (var i = 0; i < 40; i++)
            ground.Update(MobConfig.SkySpawnIntervalSeconds, overworld, drops, below);
        Assert.Equal(0, ground.LivingCount);
    }

    [Fact]
    public void AForcedSpawnPassesTheNaturalCapButNotTheHardCap()
    {
        var caps = new MobCaps();
        var manager = NewManager(caps);
        var at = new Vector3(0.5f, IslandTop + 2f, 0.5f);
        for (var i = 0; i < MobConfig.MobNaturalCap; i++)
            Assert.NotNull(manager.Spawn(Galeraptor.EntityName, at));
        Assert.False(caps.AllowsNaturalSpawn);

        while (caps.ForcedRoom > 0)
            Assert.NotNull(manager.Spawn(Cloudgolem.EntityName, at));
        Assert.Null(manager.Spawn(Galeraptor.EntityName, at));
        Assert.Null(manager.Spawn(Cloudgolem.EntityName, at));
        Assert.Equal(MobConfig.MobHardCap, caps.Living);
    }
}
