using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Sky;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation;

/// <summary>
/// The surface spawn search shared by every mob manager: samples a ring around the player (leaning toward
/// where a moving one is heading, <see cref="RingColumn"/>) and
/// returns the first dry, loaded column of a mob-spawning biome (<see cref="SpawnsMobs"/>: any land
/// biome) with two clear cells above its ground that no torch
/// lights (<see cref="IsLitByBlocks"/>) and that lies outside every battle tower's footprint (whose dark ground floor
/// is the tower skeletons' own). Main thread only (it reads the live chunk map).
/// </summary>
public static class MobSpawner
{
    /// <summary>
    /// Tries up to <paramref name="tries"/> random columns in the <paramref name="minRadius"/>..
    /// <paramref name="maxRadius"/> ring around the player. On success <paramref name="groundY"/>
    /// is the solid ground cell's Y; the caller places its body above it.
    /// </summary>
    public static bool TryFindSurfaceSpawn(ChunkManager chunks, SpawnOrigin origin, float minRadius,
        float maxRadius, int tries, out int x, out int groundY, out int z) =>
        TryFindSurfaceSpawn(chunks, origin, minRadius, maxRadius, tries, InTowerFootprint,
            out x, out groundY, out z);

    // A battle tower's footprint box: its ground floor is dark ground at the terrain height (BT-10), and its skeletons
    // own it. TowerPlacement.Meets reads the cached site verdicts; the spawn search runs on the main thread of an
    // authoritative world only (a client's mirrors never spawn).
    private static readonly Func<int, int, bool> InTowerFootprint =
        static (x, z) => WorldGenerationConfig.TowersEnabled && TowerPlacement.Meets(x, z, x, z);

    /// <summary><see cref="TryFindSurfaceSpawn(ChunkManager, SpawnOrigin, float, float, int, out int, out int, out int)"/>
    /// with the column refusal passed in: a generated column for which <paramref name="blockedColumn"/> holds never
    /// spawns (the test seam; production refuses the tower footprints).</summary>
    internal static bool TryFindSurfaceSpawn(ChunkManager chunks, SpawnOrigin origin, float minRadius,
        float maxRadius, int tries, Func<int, int, bool> blockedColumn, out int x, out int groundY, out int z)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(origin, minRadius, maxRadius, out x, out z);

            var chunkX = VoxelCoords.WorldToChunk(x);
            var chunkZ = VoxelCoords.WorldToChunk(z);
            var localX = VoxelCoords.WorldToLocal(x);
            var localZ = VoxelCoords.WorldToLocal(z);
            // The world source's column (worldgen by default): its stored water level is exactly
            // what TerrainGenerator.IsWaterColumn evaluates for a generated column
            var source = chunks.WorldSource;
            var column = source.GetColumn(chunkX, chunkZ);
            var i = VoxelCoords.ColumnIndex(localX, localZ);
            var height = column.Heights[i];

            // Surface spawns only: dry ground of a mob-spawning biome with two clear cells above,
            // all of them in loaded chunks (TryGetCell reports unloaded as absent, not as air). A
            // lake bed counts as wet even where it sits above sea level, so a mob never spawns
            // inside a pond.
            if (height < column.WaterLevel[i]) continue;
            if (!SpawnsMobs(SurfaceBiomeAt(chunks, x, z))) continue;
            if (!chunks.TryGetCell(x, height, z, out var ground, out _) || !BlockType.IsSolid(ground)) continue;
            if (!chunks.TryGetCell(x, height + 1, z, out var above1, out _) || BlockType.IsSolid(above1)) continue;
            if (!chunks.TryGetCell(x, height + 2, z, out var above2, out _) || BlockType.IsSolid(above2)) continue;
            // Never inside a battle tower: its dark ground floor passes every check above. A column a world source
            // supplies holds no generated tower.
            if (!source.Supplies(chunkX, chunkZ) && blockedColumn(x, z)) continue;
            // Torch-safe ground: never where a lamp or torch lights the cell a mob would stand in
            if (IsLitByBlocks(chunks, x, height + 1, z)) continue;

            groundY = height;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// The height of the center of a body standing on ground cell <paramref name="groundY"/>, half its height
    /// being <paramref name="halfHeight"/>: the floor plus that half, raised by the few float steps it takes for
    /// the body's bottom (center minus half) not to round to under the floor. Without that a fit test of the
    /// freshly placed body (<see cref="BoxSweep.IsColliding"/>) reads the ground it stands on as overlapping it
    /// at the terrain heights where the sum happens to round down.
    /// </summary>
    public static float StandingCenterY(int groundY, float halfHeight)
    {
        var floor = groundY + 1f;
        var center = floor + halfHeight;
        while (center - halfHeight < floor) center = MathF.BitIncrement(center);
        return center;
    }

    /// <summary>
    /// The surface biome of column (<paramref name="x"/>, <paramref name="z"/>) as spawning reads it: a
    /// generated column keeps worldgen's undithered biome (<c>ColumnData.Biomes</c> holds the dithered one),
    /// while a column the world source supplies only has its own stored biome to go by.
    /// </summary>
    public static Biome SurfaceBiomeAt(ChunkManager chunks, int x, int z)
    {
        var chunkX = VoxelCoords.WorldToChunk(x);
        var chunkZ = VoxelCoords.WorldToChunk(z);
        var source = chunks.WorldSource;
        if (!source.Supplies(chunkX, chunkZ)) return TerrainGenerator.BiomeAt(x, z);
        return source.GetColumn(chunkX, chunkZ).Biomes[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
    }

    /// <summary>
    /// The biomes whose surface the ring spawn search takes: every land biome. Only the ocean spawns nothing
    /// (its dry islands and beaches included).
    /// </summary>
    private static bool SpawnsMobs(Biome biome) => biome != Biome.Ocean;

    /// <summary>
    /// The underground counterpart of <see cref="TryFindSurfaceSpawn"/>: up to <see cref="CaveSpawnSearch.Tries"/>
    /// random columns in the <see cref="CaveSpawnSearch.MinRadius"/>..<see cref="CaveSpawnSearch.MaxRadius"/> ring
    /// around <paramref name="origin"/> (a target's eye: its feet are <c>eye.Y - Camera.EyeHeight</c>), each scanned
    /// downward from a random height within <see cref="CaveSpawnSearch.SearchHalfHeight"/> of those feet for at most
    /// <see cref="CaveSpawnSearch.ScanDepth"/> cells, taking the first solid floor with two clear, dry cells above it.
    /// The floor must lie at least <see cref="MobConfig.SlimeUndergroundDepthBlocks"/> under its column's terrain
    /// height (<see cref="SlimeSize.IsUnderground"/>), the cell above it must be dark (sky light at most
    /// <see cref="CaveSpawnSearch.MaxSkyLight"/>; a cell without a published light snapshot reads as full sun, so it is
    /// refused) and not torch-lit (<see cref="IsLitByBlocks(ChunkManager, int, int, int, int)"/> at
    /// <see cref="CaveSpawnSearch.BlockLightLimit"/>), and every cell read must be loaded. Never in hell
    /// (<see cref="HellGenerator.InHell"/>), which is the hell mobs' own ground. Last of all, only on a floor that
    /// passed every other check (so it costs nothing per frame), the standing cell's cave biome is read through
    /// <paramref name="biomes"/> (null: <see cref="CaveBiomes.At"/> over the column's stored biome and height) and
    /// the try is refused unless <see cref="CaveSpawnSearch.AllowedBiomes"/> holds it. On success
    /// <paramref name="groundY"/> is the floor cell's Y and <paramref name="biome"/> its standing cell's cave biome
    /// (<see cref="CaveBiome.Plain"/> on failure).
    /// </summary>
    public static bool TryFindCaveSpawn(ChunkManager chunks, SpawnOrigin origin, in CaveSpawnSearch search,
        CaveBiomeResolver? biomes, out int x, out int groundY, out int z, out CaveBiome biome)
    {
        var feetY = MathUtils.FloorToInt(origin.Eye.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < search.Tries; attempt++)
        {
            RingColumn(origin, search.MinRadius, search.MaxRadius, out x, out z);
            var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
            var surface = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];

            var top = feetY + Random.Shared.Next(-search.SearchHalfHeight, search.SearchHalfHeight + 1);
            // The first floor under two clear cells decides this try
            if (!TryScanFloor(chunks, x, top, z, search.ScanDepth, out var y)) continue;
            if (HellGenerator.InHell(y) || !SlimeSize.IsUnderground(y, surface)) continue;
            if (IsLitByBlocks(chunks, x, y + 1, z, search.BlockLightLimit)) continue;
            if ((int)MathF.Round(EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 1.5f, z + 0.5f)).Sun * 15f)
                > search.MaxSkyLight) continue;
            // The cave biome last: three noise blends, paid only by an otherwise accepted floor
            biome = (biomes ?? DefaultCaveBiomes)(chunks, x, y + 1, z);
            if (!CaveSpawnRules.Allows(search.AllowedBiomes, biome)) continue;

            groundY = y;
            return true;
        }

        x = groundY = z = 0;
        biome = CaveBiome.Plain;
        return false;
    }

    /// <summary>The production <see cref="CaveBiomeResolver"/>: <see cref="CaveBiomes.At"/> at the cell over its
    /// column's stored (dithered) surface biome and terrain height, read from the world source.</summary>
    internal static readonly CaveBiomeResolver DefaultCaveBiomes = CaveBiomeAt;

    private static CaveBiome CaveBiomeAt(ChunkManager chunks, int x, int y, int z)
    {
        var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
        var i = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z));
        return CaveBiomes.At(x, y, z, column.Biomes[i], column.Heights[i]);
    }

    /// <summary>Whether the feet under <paramref name="eye"/> stand underground: well under their own column's
    /// terrain height (<see cref="SlimeSize.IsUnderground"/> at the cell under the feet). The cave searches run
    /// first, or only, around such a target.</summary>
    internal static bool TargetUnderground(ChunkManager chunks, Vector3 eye)
    {
        var x = MathUtils.FloorToInt(eye.X);
        var z = MathUtils.FloorToInt(eye.Z);
        var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
        var surface = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
        return SlimeSize.IsUnderground(MathUtils.FloorToInt(eye.Y - Camera.EyeHeight) - 1, surface);
    }

    /// <summary>
    /// The hell mobs' search: up to <paramref name="tries"/> random columns in the ring around the
    /// player, each scanned down from a random height within <paramref name="searchHalfHeight"/> of the
    /// player's feet for at most <paramref name="scanDepth"/> cells, taking the first open cavern floor
    /// (solid ground under two clear, dry cells, every cell loaded). The floor and both cells above it
    /// must lie in hell (<see cref="HellGenerator.InHell"/>), so nothing is ever found in the overworld or
    /// the barrier band, and the standing cell's block light must stay at or under
    /// <see cref="MobConfig.HellSpawnMaxBlockLight"/> (a lava-lit pool edge or a torched base stays
    /// clear; hell has no skylight). On success <paramref name="groundY"/> is the floor cell's Y.
    /// </summary>
    public static bool TryFindHellSpawn(ChunkManager chunks, SpawnOrigin origin, float minRadius,
        float maxRadius, int tries, int searchHalfHeight, int scanDepth, out int x, out int groundY, out int z)
    {
        var feetY = MathUtils.FloorToInt(origin.Eye.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(origin, minRadius, maxRadius, out x, out z);
            var top = feetY + Random.Shared.Next(-searchHalfHeight, searchHalfHeight + 1);
            if (!TryScanFloor(chunks, x, top, z, scanDepth, out var y)) continue;
            if (!HellGenerator.InHell(y + 2)) continue;
            var light = EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 1.5f, z + 0.5f));
            if ((int)MathF.Round(light.Block * 15f) > MobConfig.HellSpawnMaxBlockLight) continue;

            groundY = y;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>Whether a body standing at height <paramref name="feetY"/> is in the sky islands' band
    /// (<see cref="WorldGenerationConfig.SkyIslandBottomY"/>..<see cref="WorldGenerationConfig.SkyIslandTopY"/>),
    /// the only place the sky mobs spawn around.</summary>
    public static bool InSkyBand(int feetY) =>
        feetY >= WorldGenerationConfig.SkyIslandBottomY && feetY <= WorldGenerationConfig.SkyIslandTopY;

    /// <summary>
    /// The sky mobs' search: up to <paramref name="tries"/> random columns in the ring around the player, each
    /// scanned down from a random height within <paramref name="searchHalfHeight"/> of the player's feet for at
    /// most <paramref name="scanDepth"/> cells, taking the first open floor (solid ground under two clear, dry
    /// cells, every cell loaded). The floor and both cells above it must lie in the sky band
    /// (<see cref="InSkyBand"/>), so neither the cloud sea under the islands nor the overworld or hell is ever
    /// found, the column must lie inside an island zone (<see cref="SkyIslands.ZoneAt"/>), and no torch may
    /// light the standing cell (<see cref="IsLitByBlocks"/>). On success <paramref name="groundY"/> is the
    /// island top's Y.
    /// </summary>
    public static bool TryFindSkySpawn(ChunkManager chunks, SpawnOrigin origin, float minRadius,
        float maxRadius, int tries, int searchHalfHeight, int scanDepth, out int x, out int groundY, out int z)
    {
        var feetY = MathUtils.FloorToInt(origin.Eye.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(origin, minRadius, maxRadius, out x, out z);
            var top = feetY + Random.Shared.Next(-searchHalfHeight, searchHalfHeight + 1);
            if (!TryScanFloor(chunks, x, top, z, scanDepth, out var y)) continue;
            if (!InSkyBand(y) || !InSkyBand(y + 2)) continue;
            if (!SkyIslands.ZoneAt(x + 0.5f, z + 0.5f).InZone || IsLitByBlocks(chunks, x, y + 1, z)) continue;

            groundY = y;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// A random column in the <paramref name="minRadius"/>..<paramref name="maxRadius"/> ring around
    /// <paramref name="origin"/>. Around one moving at <see cref="MobConfig.MobSpawnAheadMinSpeed"/> or faster,
    /// <see cref="MobConfig.MobSpawnAheadShare"/> of the columns fall inside the arc
    /// <see cref="MobConfig.MobSpawnAheadHalfAngleDegrees"/> to either side of its heading, so a running player
    /// meets what spawns instead of leaving it behind; the rest, and every column around a slow or standing
    /// one, are spread over the whole circle.
    /// </summary>
    internal static void RingColumn(SpawnOrigin origin, float minRadius, float maxRadius, out int x, out int z)
    {
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var velocity = origin.Velocity;
        var speedSquared = velocity.X * velocity.X + velocity.Z * velocity.Z;
        if (speedSquared >= MobConfig.MobSpawnAheadMinSpeed * MobConfig.MobSpawnAheadMinSpeed
            && Random.Shared.NextSingle() < MobConfig.MobSpawnAheadShare)
        {
            var spread = MobConfig.MobSpawnAheadHalfAngleDegrees * (MathF.PI / 180f);
            angle = MathF.Atan2(velocity.Z, velocity.X) + (Random.Shared.NextSingle() * 2f - 1f) * spread;
        }

        var radius = MathUtils.Lerp(minRadius, maxRadius, Random.Shared.NextSingle());
        x = MathUtils.FloorToInt(origin.Eye.X + MathF.Cos(angle) * radius);
        z = MathUtils.FloorToInt(origin.Eye.Z + MathF.Sin(angle) * radius);
    }

    // Scans column (x, z) down from top for at most scanDepth cells to the first solid floor with two
    // clear, dry cells above it; false at an unloaded cell, or when the scan ends without one.
    private static bool TryScanFloor(ChunkManager chunks, int x, int top, int z, int scanDepth, out int floorY)
    {
        var clearAbove = 0;
        for (var y = top; y > top - scanDepth; y--)
        {
            if (!chunks.TryGetCell(x, y, z, out var id, out _)) break;
            if (BlockType.IsFluid(id))
            {
                clearAbove = 0;
                continue;
            }
            if (!BlockType.IsSolid(id))
            {
                clearAbove++;
                continue;
            }
            if (clearAbove >= 2)
            {
                floorY = y;
                return true;
            }
            clearAbove = 0;
        }

        floorY = 0;
        return false;
    }

    /// <summary>
    /// Whether the block light (torches, lamps, lava) in cell (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) reaches <see cref="MobConfig.MobSpawnBlockLightLimit"/>, which
    /// keeps every ring spawn off it, so a few torches keep a base clear. Read through
    /// <see cref="EntityLight.Sample"/> from the light each chunk publishes with its mesh (a headless
    /// server publishes it too); a cell without one reads as unlit.
    /// </summary>
    public static bool IsLitByBlocks(ChunkManager chunks, int x, int y, int z) =>
        IsLitByBlocks(chunks, x, y, z, MobConfig.MobSpawnBlockLightLimit);

    /// <summary><see cref="IsLitByBlocks(ChunkManager, int, int, int)"/> against <paramref name="limit"/> (0..15)
    /// instead of the shared torch-safe limit.</summary>
    public static bool IsLitByBlocks(ChunkManager chunks, int x, int y, int z, int limit)
    {
        var light = EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
        return (int)MathF.Round(light.Block * 15f) >= limit;
    }
}
