using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Data;
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
/// biome) with two clear cells above its ground that no man-made light
/// lights (<see cref="IsLitByManMadeLight(ChunkManager, int, int, int)"/>) and that lies outside every battle tower's footprint (whose dark ground floor
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
            // Torch-safe ground: never where man-made light (a torch, a lamp, a lit station) lights the cell a mob
            // would stand in
            if (IsLitByManMadeLight(chunks, x, height + 1, z)) continue;

            groundY = height;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// The sea's counterpart of <see cref="TryFindSurfaceSpawn(ChunkManager, SpawnOrigin, float, float, int, out int, out int, out int)"/>:
    /// up to <paramref name="tries"/> random columns in the ring whose terrain lies under its water level (the sea, a
    /// lake, a river) and whose top water cell is loaded and wet. On success <paramref name="bedY"/> is the
    /// bed Y, <paramref name="waterTopY"/> the Y of the column top water cell and <paramref name="biome"/> its
    /// biome; the caller sizes its body to the depth between them.
    /// </summary>
    public static bool TryFindWaterSpawn(ChunkManager chunks, SpawnOrigin origin, float minRadius, float maxRadius,
        int tries, out int x, out int bedY, out int waterTopY, out int z, out Biome biome)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(origin, minRadius, maxRadius, out x, out z);
            var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
            var i = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z));
            var height = column.Heights[i];
            var level = column.WaterLevel[i];
            if (height >= level) continue;
            // The top water cell must be loaded and wet, or the column is not (yet) flooded in the live map
            if (!chunks.TryGetCell(x, level, z, out var top, out _) || !BlockType.IsFluid(top)) continue;

            bedY = height;
            waterTopY = level;
            biome = SurfaceBiomeAt(chunks, x, z);
            return true;
        }

        x = bedY = waterTopY = z = 0;
        biome = Biome.Ocean;
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
    /// Whether surface water (a lake, a river, the sea: a column whose terrain lies under its water level) is
    /// within <paramref name="radius"/> blocks of column (<paramref name="x"/>, <paramref name="z"/>), sampled at
    /// eight headings on that radius and on half of it; the waterside animals spawn only there.
    /// </summary>
    public static bool IsNearSurfaceWater(ChunkManager chunks, int x, int z, float radius)
    {
        for (var ring = 1; ring <= 2; ring++)
        for (var heading = 0; heading < 8; heading++)
        {
            var angle = heading * (MathF.Tau / 8f);
            var sx = MathUtils.FloorToInt(x + 0.5f + MathF.Cos(angle) * radius * ring * 0.5f);
            var sz = MathUtils.FloorToInt(z + 0.5f + MathF.Sin(angle) * radius * ring * 0.5f);
            var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(sx), VoxelCoords.WorldToChunk(sz));
            var i = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(sx), VoxelCoords.WorldToLocal(sz));
            if (column.Heights[i] < column.WaterLevel[i]) return true;
        }
        return false;
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
    /// refused) and not lit by man-made light (<see cref="IsLitByManMadeLight(ChunkManager, int, int, int, int)"/> at
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
            if (IsLitByManMadeLight(chunks, x, y + 1, z, search.BlockLightLimit)) continue;
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
    /// the barrier band, and the standing cell's man-made light must stay at or under
    /// <see cref="MobConfig.HellSpawnMaxBlockLight"/> (<see cref="IsLitByManMadeLight(ChunkManager, int, int, int, int)"/>:
    /// a torched base stays clear, a lava-lit pool edge does not; hell has no skylight). On success <paramref name="groundY"/> is the floor cell's Y.
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
            if (IsLitByManMadeLight(chunks, x, y + 1, z, MobConfig.HellSpawnMaxBlockLight + 1)) continue;

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
    /// found, the column must lie inside an island zone (<see cref="SkyIslands.ZoneAt"/>), and no man-made light may
    /// light the standing cell (<see cref="IsLitByManMadeLight(ChunkManager, int, int, int)"/>). On success <paramref name="groundY"/> is the
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
            if (!SkyIslands.ZoneAt(x + 0.5f, z + 0.5f).InZone || IsLitByManMadeLight(chunks, x, y + 1, z)) continue;

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
    /// Whether man-made light in cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) reaches
    /// <see cref="MobConfig.MobSpawnBlockLightLimit"/>, which keeps every ring spawn off it, so a few torches keep a
    /// base clear. See <see cref="IsLitByManMadeLight(ChunkManager, int, int, int, int)"/>.
    /// </summary>
    public static bool IsLitByManMadeLight(ChunkManager chunks, int x, int y, int z) =>
        IsLitByManMadeLight(chunks, x, y, z, MobConfig.MobSpawnBlockLightLimit);

    /// <summary>
    /// Whether man-made light reaches level <paramref name="limit"/> (1..15) in cell (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="z"/>): a block whose emission is man-made
    /// (<see cref="BlockType.ManMadeEmission"/>: a torch, a lamp; never <c>light.natural</c> glowing plants, crystals
    /// or lava) or a burning station's point light (<see cref="ChunkManager.ManMadePointLights"/>, as bright as a
    /// torch: <see cref="StationLightEmission"/>). Light falls off by one per step through light-passing cells,
    /// exactly as the voxel block light spreads, so a flood out from the cell to the depth such light could still
    /// reach it finds every source that lights it at the limit or brighter and none behind a wall. The
    /// natural light around the cell never counts, however bright, and an unloaded cell stops the flood.
    /// </summary>
    public static bool IsLitByManMadeLight(ChunkManager chunks, int x, int y, int z, int limit)
    {
        var reach = Math.Max(MaxEmission, StationLightEmission) - limit;
        if (reach < 0) return false;

        // Only the burning stations close enough to matter are looked up per flooded cell
        var stations = _stations ??= [];
        stations.Clear();
        chunks.ManMadePointLights?.AppendLitCells(stations);
        var stationReach = StationLightEmission - limit;
        stations.RemoveAll(c => Math.Abs(c.X - x) + Math.Abs(c.Y - y) + Math.Abs(c.Z - z) > stationReach);

        var visited = _visited ??= [];
        var queue = _queue ??= new Queue<(int X, int Y, int Z, int Depth)>();
        visited.Clear();
        queue.Clear();
        visited.Add((x, y, z));
        queue.Enqueue((x, y, z, 0));
        while (queue.TryDequeue(out var cell))
        {
            if (!chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out _)) continue;
            if (BlockType.ManMadeEmission[id] - cell.Depth >= limit) return true;
            if (StationLightEmission - cell.Depth >= limit && stations.Contains((cell.X, cell.Y, cell.Z))) return true;
            // An opaque emitter (a lamp) lights its neighbors but passes nothing on; the starting cell always floods
            if (cell.Depth == reach || (cell.Depth > 0 && !BlockType.PassesLightById[id])) continue;
            foreach (var (dx, dy, dz) in Vector3Directions.CardinalOffsets)
            {
                var next = (cell.X + dx, cell.Y + dy, cell.Z + dz);
                if (visited.Add(next)) queue.Enqueue((next.Item1, next.Item2, next.Item3, cell.Depth + 1));
            }
        }
        return false;
    }

    // The brightest block emission, and the man-made point lights' level: a torch's (FireLight strength 1)
    private const int MaxEmission = 15;
    internal const int StationLightEmission = 14;

    // Flood scratch, per thread: the spawn searches run on an authoritative world's main thread, and the tests
    // run several worlds side by side
    [ThreadStatic] private static List<(int X, int Y, int Z)>? _stations;
    [ThreadStatic] private static HashSet<(int X, int Y, int Z)>? _visited;
    [ThreadStatic] private static Queue<(int X, int Y, int Z, int Depth)>? _queue;
}
