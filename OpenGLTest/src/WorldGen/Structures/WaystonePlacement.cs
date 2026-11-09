using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Sky;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// Where the waystone structures stand: beside ruins and, on the surface, out from a battle tower's entrance (plans/WAYSTONES.md). Every ruin a <see cref="RuinPlacement"/> places
/// may get one satellite a short walk away: its chance rolls from a salted hash of the ruin's own, its template is one of the
/// satellites of the ruin's kind, and up to <see cref="WorldFeatureConfig.WaystoneSiteTries"/> rolled distances and directions
/// are tried in order, the first whose site passes that realm's ruin rule winning (level dry land, a sealed chamber at a
/// hashed depth, a level pocket floor in the same hell layer, the same island's top). A pure function of (ruin cell, seed):
/// every chunk the footprint crosses recomputes the ruin and its satellite and stamps its own part, so the seams agree with no
/// cross-chunk writes. A satellite never overlaps its own ruin's footprint.
/// </summary>
public static class WaystonePlacement
{
    private const int SatelliteSalt = 0x5E7E;

    // The widest clearance two footprints can need: the largest pair of halves plus the gap
    private const int LargestClearance = 2 * (StructureRegistry.MaxSize / 2) + WorldFeatureConfig.WaystoneRuinGap;

    /// <summary>The farthest a satellite's center can stand from its ruin's: the rolled distance, or the push that clears the
    /// largest pair of footprints on a diagonal.</summary>
    private static readonly double MaxCenterDistance =
        Math.Max(WorldFeatureConfig.WaystoneMaxDistance, (LargestClearance + 0.5) * Math.Sqrt(2));

    // How far past a chunk's columns a ruin's center may lie and still put its satellite's footprint into the chunk
    private static readonly int CellReach = (int)Math.Ceiling(MaxCenterDistance) + 1 + RuinPlacement.Reach;

    // ---- Surface ----

    /// <summary>The world-y band <see cref="StampSurface"/> can write: the surface ruins' band.</summary>
    internal static StampBand SurfaceBand(int neighborhoodMaxHeight) => RuinPlacement.SurfaceBand(neighborhoodMaxHeight);

    /// <summary>Stamps every surface ruin's waystone whose footprint reaches the chunk.</summary>
    public static void StampSurface(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.SatellitesOf(StructureKind.Surface).Count == 0) return;
        foreach (var (cellX, cellZ) in RuinPlacement.CellsReaching(worldX0, worldZ0, CellReach))
            if (TryFindSurface(cellX, cellZ, (worldX0, worldZ0), out _, out var waystone))
                Stamp(blocks, ref state, worldX0, worldY0, worldZ0, waystone);
        StampTowers(blocks, ref state, worldX0, worldY0, worldZ0);
    }

    /// <summary>The surface ruin of a ruin cell and its waystone: false when the cell holds no ruin, the ruin no satellite, or
    /// no tried site is level, dry land.</summary>
    internal static bool TryFindSurface(int cellX, int cellZ, out PlacedStructure ruin, out PlacedStructure waystone) =>
        TryFindSurface(cellX, cellZ, null, out ruin, out waystone);

    private static bool TryFindSurface(int cellX, int cellZ, (int X0, int Z0)? chunk, out PlacedStructure ruin,
        out PlacedStructure waystone)
    {
        ruin = waystone = default;
        if (!TryRollSatellite(StructureKind.Surface, cellX, cellZ, 0, chunk, out var hash, out var sat, out var rx, out var rz)
            || !RuinPlacement.TryFindSurfaceSite(rx, rz, hash, out var ruinTemplate, out var ruinGround))
            return false;
        ruin = new PlacedStructure(ruinTemplate, RuinPlacement.TurnsOf(hash), rx, ruinGround + 1 - ruinTemplate.GroundLayer, rz);

        // The ruin's biome picks the template: the satellite is a short walk off
        if (!StructureRegistry.TryPick(StructureKind.Surface, TerrainGenerator.BiomeAt(rx, rz), SplitMix64.Unit(sat, 4),
                out var template, satellite: true))
            return false;
        var clearance = Clearance(ruinTemplate, template);
        for (var attempt = 0; attempt < WorldFeatureConfig.WaystoneSiteTries; attempt++)
        {
            if (!TryCenter(rx, rz, TryHash(sat, attempt), clearance, WorldFeatureConfig.WaystoneMinDistance,
                    WorldFeatureConfig.WaystoneMaxDistance, MaxCenterDistance, out var x, out var z)
                || TerrainGenerator.BiomeAt(x, z) == Biome.Ocean
                || !RuinPlacement.TrySurfaceGround(template, x, z, out var ground))
                continue;
            waystone = new PlacedStructure(template, RuinPlacement.TurnsOf(sat), x, ground + 1 - template.GroundLayer, z);
            return true;
        }
        return false;
    }

    // ---- Battle towers ----

    // How far past a chunk's columns a tower's center may lie and still put its waystone's footprint into the chunk: the
    // tower's reach, the waystone's whole footprint, the gap, the rolled extra distance and a column of rounding slack
    private static readonly int TowerCellReach = TowerPlacement.Reach + StructureRegistry.MaxSize + WorldFeatureConfig.WaystoneRuinGap
        + (int)Math.Ceiling(WorldFeatureConfig.WaystoneTowerExtraDistance) + 2;

    // Stamps every tower's waystone whose footprint reaches the chunk
    private static void StampTowers(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var maxX = worldX0 + Chunk.Size - 1;
        var maxZ = worldZ0 + Chunk.Size - 1;
        for (var cellX = TowerPlacement.CellOf(worldX0 - TowerCellReach); cellX <= TowerPlacement.CellOf(maxX + TowerCellReach); cellX++)
        for (var cellZ = TowerPlacement.CellOf(worldZ0 - TowerCellReach); cellZ <= TowerPlacement.CellOf(maxZ + TowerCellReach); cellZ++)
            if (TryFindTower(cellX, cellZ, (worldX0, worldZ0), out _, out var waystone))
                Stamp(blocks, ref state, worldX0, worldY0, worldZ0, waystone);
    }

    /// <summary>The battle tower of a tower cell and its waystone, standing out from the tower's entrance on level dry land:
    /// false when the cell holds no tower, the tower no waystone, or no tried site is level, dry land.</summary>
    internal static bool TryFindTower(int cellX, int cellZ, out TowerSite tower, out PlacedStructure waystone) =>
        TryFindTower(cellX, cellZ, null, out tower, out waystone);

    private static bool TryFindTower(int cellX, int cellZ, (int X0, int Z0)? chunk, out TowerSite tower,
        out PlacedStructure waystone)
    {
        tower = null!;
        waystone = default;
        // A constant && keeps the compiler from flagging the rest as unreachable while towers are switched off
        if (!(WorldGenerationConfig.TowersEnabled && TowerPlacement.TryRoll(cellX, cellZ, out var hash, out var cx, out var cz, out _)))
            return false;
        var sat = SatelliteHash(hash);
        if (SplitMix64.Unit(sat, 0) >= WorldFeatureConfig.WaystoneTowerChance
            || chunk is { } c && !TowerMayReach(cx, cz, c.X0, c.Z0)
            || !TowerPlacement.TryGetSite(cellX, cellZ, out tower)
            || !StructureRegistry.TryPick(StructureKind.Surface, TerrainGenerator.BiomeAt(cx, cz), SplitMix64.Unit(sat, 4),
                out var template, satellite: true))
            return false;
        var entrance = EntranceAngle(tower);
        var clearance = tower.OuterRadius + template.Half + WorldFeatureConfig.WaystoneRuinGap;
        for (var attempt = 0; attempt < WorldFeatureConfig.WaystoneSiteTries; attempt++)
        {
            var tryHash = TryHash(sat, attempt);
            var angle = entrance + (SplitMix64.Unit(tryHash, 1) * 2 - 1) * WorldFeatureConfig.WaystoneTowerSpreadRadians;
            var (dx, dz) = (Math.Cos(angle), Math.Sin(angle));
            var d = PushFor(clearance, dx, dz) + SplitMix64.Unit(tryHash, 3) * WorldFeatureConfig.WaystoneTowerExtraDistance;
            var x = tower.CenterX + (int)Math.Round(dx * d, MidpointRounding.AwayFromZero);
            var z = tower.CenterZ + (int)Math.Round(dz * d, MidpointRounding.AwayFromZero);
            if (TerrainGenerator.BiomeAt(x, z) == Biome.Ocean || !RuinPlacement.TrySurfaceGround(template, x, z, out var ground))
                continue;
            waystone = new PlacedStructure(template, RuinPlacement.TurnsOf(sat), x, ground + 1 - template.GroundLayer, z);
            return true;
        }
        return false;
    }

    // The angle (in the X, Z plane, as Atan2(dz, dx)) straight out of the tower's entrance: the middle of the entrance strips
    // (TowerShell.StepOf turns the strips by the site's quarter turns)
    private static double EntranceAngle(TowerSite s) =>
        2 * Math.PI * (1 - WorldGenerationConfig.TowerEntranceSteps / (2.0 * WorldGenerationConfig.TowerStairStepsPerTurn) - s.Turns * 0.25);

    // Whether a tower centered at (cx, cz) can put its waystone into the chunk, before any site is searched
    private static bool TowerMayReach(int cx, int cz, int worldX0, int worldZ0) =>
        cx + TowerCellReach >= worldX0 && cx - TowerCellReach <= worldX0 + Chunk.Size - 1
        && cz + TowerCellReach >= worldZ0 && cz - TowerCellReach <= worldZ0 + Chunk.Size - 1;

    // ---- Underground ----

    /// <summary>The world-y band <see cref="StampUnderground"/> can write: the underground rooms' band.</summary>
    internal static StampBand UndergroundBand(int neighborhoodMaxHeight) => RuinPlacement.UndergroundBand(neighborhoodMaxHeight);

    /// <summary>Stamps every underground room's waystone chamber whose footprint reaches the chunk.</summary>
    public static void StampUnderground(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.SatellitesOf(StructureKind.Underground).Count == 0) return;
        foreach (var (cellX, cellZ) in RuinPlacement.CellsReaching(worldX0, worldZ0, CellReach))
            if (TryFindUnderground(cellX, cellZ, (worldX0, worldZ0), out _, out var waystone))
                Stamp(blocks, ref state, worldX0, worldY0, worldZ0, waystone);
    }

    /// <summary>The underground room of a ruin cell and its waystone: a sealed chamber of its own at a depth its hash picks.</summary>
    internal static bool TryFindUnderground(int cellX, int cellZ, out PlacedStructure ruin, out PlacedStructure waystone) =>
        TryFindUnderground(cellX, cellZ, null, out ruin, out waystone);

    private static bool TryFindUnderground(int cellX, int cellZ, (int X0, int Z0)? chunk, out PlacedStructure ruin,
        out PlacedStructure waystone)
    {
        ruin = waystone = default;
        if (!TryRollSatellite(StructureKind.Underground, cellX, cellZ, 0, chunk, out var hash, out var sat, out var rx, out var rz)
            || !RuinPlacement.TryFindUndergroundSite(rx, rz, hash, out var ruinTemplate, out var ruinOrigin))
            return false;
        ruin = new PlacedStructure(ruinTemplate, RuinPlacement.TurnsOf(hash), rx, ruinOrigin, rz);

        if (!StructureRegistry.TryPick(StructureKind.Underground, null, SplitMix64.Unit(sat, 4), out var template, satellite: true))
            return false;
        var clearance = Clearance(ruinTemplate, template);
        for (var attempt = 0; attempt < WorldFeatureConfig.WaystoneSiteTries; attempt++)
        {
            var tryHash = TryHash(sat, attempt);
            if (!TryCenter(rx, rz, tryHash, clearance, WorldFeatureConfig.WaystoneMinDistance,
                    WorldFeatureConfig.WaystoneMaxDistance, MaxCenterDistance, out var x, out var z)
                || !RuinPlacement.TryUndergroundOrigin(template, x, z, tryHash, out var originY))
                continue;
            waystone = new PlacedStructure(template, RuinPlacement.TurnsOf(sat), x, originY, z);
            return true;
        }
        return false;
    }

    // ---- Hell ----

    /// <summary>Stamps every hell ruin's waystone whose footprint reaches the chunk, in each hell layer the chunk spans; its
    /// cells stay inside the layer.</summary>
    public static void StampHell(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.SatellitesOf(StructureKind.Hell).Count == 0) return;
        if (!RuinPlacement.TryHellLayers(worldY0, out var first, out var last)) return;
        for (var layer = first; layer <= last; layer++)
        foreach (var (cellX, cellZ) in RuinPlacement.CellsReaching(worldX0, worldZ0, CellReach))
        {
            if (!TryFindHell(cellX, cellZ, layer, (worldX0, worldZ0), out _, out var w)) continue;
            RuinPlacement.StampInLayer(blocks, ref state, worldX0, worldY0, worldZ0, w.Template, w.Turns, w.X, w.OriginY, w.Z, layer);
        }
    }

    /// <summary>The hell ruin of a ruin cell in <paramref name="layer"/> and its waystone: on the highest pocket floor of its
    /// column in the same layer that is level within the template's foundation layers, clear above.</summary>
    internal static bool TryFindHell(int cellX, int cellZ, int layer, out PlacedStructure ruin, out PlacedStructure waystone) =>
        TryFindHell(cellX, cellZ, layer, null, out ruin, out waystone);

    private static bool TryFindHell(int cellX, int cellZ, int layer, (int X0, int Z0)? chunk, out PlacedStructure ruin,
        out PlacedStructure waystone)
    {
        ruin = waystone = default;
        if (!TryRollSatellite(StructureKind.Hell, cellX, cellZ, layer, chunk, out var hash, out var sat, out var rx, out var rz)
            || !RuinPlacement.TryFindHellSite(rx, rz, layer, hash, out var ruinTemplate, out var ruinFloor))
            return false;
        ruin = new PlacedStructure(ruinTemplate, RuinPlacement.TurnsOf(hash), rx, ruinFloor - ruinTemplate.GroundLayer, rz);

        if (!StructureRegistry.TryPick(StructureKind.Hell, null, SplitMix64.Unit(sat, 4), out var template, satellite: true))
            return false;
        var clearance = Clearance(ruinTemplate, template);
        for (var attempt = 0; attempt < WorldFeatureConfig.WaystoneSiteTries; attempt++)
        {
            // Scanned from the pocket's top, so the waystone takes any floor its column has, level within the layers its
            // template fills under its ground layer (hell floors are rough; a strictly level one is rare)
            if (!TryCenter(rx, rz, TryHash(sat, attempt), clearance, WorldFeatureConfig.WaystoneMinDistance,
                    WorldFeatureConfig.WaystoneMaxDistance, MaxCenterDistance, out var x, out var z)
                || !RuinPlacement.TryHellFloor(template, x, z, layer, 0f, template.GroundLayer, out var floorY))
                continue;
            waystone = new PlacedStructure(template, RuinPlacement.TurnsOf(sat), x, floorY - template.GroundLayer, z);
            return true;
        }
        return false;
    }

    // ---- Sky ----

    /// <summary>The world-y band <see cref="StampSky"/> can write: the island band.</summary>
    internal static StampBand SkyBand() => RuinPlacement.SkyBand();

    /// <summary>Stamps every sky ruin's waystone whose footprint reaches the chunk. A waystone stays inside its island's disc,
    /// so the islands the ruins collect are all it needs.</summary>
    public static void StampSky(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        if (StructureRegistry.SatellitesOf(StructureKind.Sky).Count == 0) return;
        Span<SkyIsland> islands = stackalloc SkyIsland[SkyIslands.MaxIslandsPerBox];
        var count = RuinPlacement.CollectIslandsReaching(worldX0, worldZ0, islands);
        for (var k = 0; k < count; k++)
            if (TryFindSky(islands[k], out _, out var waystone))
                Stamp(blocks, ref state, worldX0, worldY0, worldZ0, waystone);
    }

    /// <summary>The ruin an island holds and its waystone, on the same island's top within its disc: false when the island
    /// holds no ruin, the ruin no satellite, or no tried footprint is on the island and level. An island is small, so the
    /// distance is rolled across the room it has rather than the overworld's range.</summary>
    internal static bool TryFindSky(in SkyIsland island, out PlacedStructure ruin, out PlacedStructure waystone)
    {
        ruin = waystone = default;
        var hash = RuinPlacement.SkyHash(island);
        var sat = SatelliteHash(hash);
        if (!RuinPlacement.SkyRuinRolls(hash) || !Rolls(sat)
            || !RuinPlacement.TryFindSkySite(island, out var ruinTemplate, out var ruinTurns, out var rx, out var rz, out var top))
            return false;
        ruin = new PlacedStructure(ruinTemplate, ruinTurns, rx, top + 1 - ruinTemplate.GroundLayer, rz);

        if (!StructureRegistry.TryPick(StructureKind.Sky, null, SplitMix64.Unit(sat, 4), out var template, satellite: true))
            return false;
        // The footprint stays inside the island's nominal disc on both axes, with columns of slack for the rounding of both
        // centers, so collecting the island (for a chunk, or a single column) covers every column the footprint reaches
        var room = Math.Min(MaxCenterDistance, island.Radius - template.Half - 2);
        var clearance = Clearance(ruinTemplate, template);
        for (var attempt = 0; attempt < WorldFeatureConfig.WaystoneSiteTries; attempt++)
        {
            if (!TryCenter(rx, rz, TryHash(sat, attempt), clearance, 0, room, room, out var x, out var z)
                || !RuinPlacement.TrySkyTop(island, template, x, z, out var satTop))
                continue;
            waystone = new PlacedStructure(template, RuinPlacement.TurnsOf(sat), x, satTop + 1 - template.GroundLayer, z);
            return true;
        }
        return false;
    }

    // ---- Shared ----

    /// <summary>The satellite hash of a ruin: its chance, template and turn roll from it, and its first try's site.</summary>
    internal static ulong SatelliteHash(ulong ruinHash) => SplitMix64.Mix(ruinHash ^ SatelliteSalt);

    // The hash one try rolls its distance, direction and depth from: the first try's is the satellite hash itself
    private static ulong TryHash(ulong sat, int attempt) => attempt == 0 ? sat : SplitMix64.Mix(sat + (ulong)attempt);

    private static bool Rolls(ulong sat) => SplitMix64.Unit(sat, 0) < WorldFeatureConfig.WaystoneSatelliteChance;

    // A cell's ruin roll and its satellite's, cheapest first, and (for a chunk's stamp) whether any try can reach the chunk at
    // all, all before any site is searched
    private static bool TryRollSatellite(StructureKind kind, int cellX, int cellZ, int layer, (int X0, int Z0)? chunk,
        out ulong hash, out ulong sat, out int rx, out int rz)
    {
        sat = 0;
        if (!RuinPlacement.TryRollRuin(kind, cellX, cellZ, layer, out hash, out rx, out rz)) return false;
        sat = SatelliteHash(hash);
        return Rolls(sat) && (chunk is not { } c || MayReach(rx, rz, sat, c.X0, c.Z0));
    }

    // Columns a satellite's center keeps from its ruin's on the dominant axis: both halves and the gap
    private static int Clearance(StructureTemplate ruin, StructureTemplate satellite) =>
        ruin.Half + satellite.Half + WorldFeatureConfig.WaystoneRuinGap;

    private static (double Dx, double Dz, double Distance) Direction(ulong tryHash, double minDistance, double maxDistance)
    {
        var angle = SplitMix64.Unit(tryHash, 2) * (2 * Math.PI);
        return (Math.Cos(angle), Math.Sin(angle), minDistance + SplitMix64.Unit(tryHash, 1) * (maxDistance - minDistance));
    }

    // The distance along (dx, dz) at which the dominant axis offset reaches clearance + 0.5, so it rounds past clearance
    private static double PushFor(int clearance, double dx, double dz) =>
        (clearance + 0.5) / Math.Max(Math.Abs(dx), Math.Abs(dz));

    /// <summary>One try's center column: a distance rolled in <paramref name="minDistance"/>..<paramref name="maxDistance"/>
    /// along a rolled direction from the ruin's center, pushed out until the two footprints keep
    /// <see cref="WorldFeatureConfig.WaystoneRuinGap"/> columns apart. False past <paramref name="limit"/>.</summary>
    private static bool TryCenter(int ruinX, int ruinZ, ulong tryHash, int clearance, double minDistance, double maxDistance,
        double limit, out int x, out int z)
    {
        var (dx, dz, d) = Direction(tryHash, minDistance, maxDistance);
        d = Math.Max(d, PushFor(clearance, dx, dz));
        x = ruinX + (int)Math.Round(dx * d, MidpointRounding.AwayFromZero);
        z = ruinZ + (int)Math.Round(dz * d, MidpointRounding.AwayFromZero);
        return d <= limit;
    }

    // Whether any try's footprint can touch the chunk wherever the push puts it along its ray (up to the push of the largest
    // footprints), so a chunk out of reach never searches the ruin's site
    private static bool MayReach(int ruinX, int ruinZ, ulong sat, int worldX0, int worldZ0)
    {
        const int slack = RuinPlacement.Reach + 1;
        for (var attempt = 0; attempt < WorldFeatureConfig.WaystoneSiteTries; attempt++)
        {
            var (dx, dz, d) = Direction(TryHash(sat, attempt), WorldFeatureConfig.WaystoneMinDistance,
                WorldFeatureConfig.WaystoneMaxDistance);
            var far = Math.Max(d, PushFor(LargestClearance, dx, dz));
            if (ruinX + Math.Max(dx * d, dx * far) + slack >= worldX0
                && ruinX + Math.Min(dx * d, dx * far) - slack <= worldX0 + Chunk.Size - 1
                && ruinZ + Math.Max(dz * d, dz * far) + slack >= worldZ0
                && ruinZ + Math.Min(dz * d, dz * far) - slack <= worldZ0 + Chunk.Size - 1)
                return true;
        }
        return false;
    }

    private static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0, in PlacedStructure w) =>
        RuinPlacement.StampAt(blocks, ref state, worldX0, worldY0, worldZ0, w.Template, w.Turns, w.X, w.OriginY, w.Z,
            int.MinValue, int.MaxValue);
}
