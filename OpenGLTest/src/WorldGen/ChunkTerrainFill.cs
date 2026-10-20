using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.WorldGen;

/// <summary>The worldgen fill of a fresh <see cref="Chunk"/>: column fill, carvers, water, mud, lava and every feature stamp, written straight into the chunk's arrays (never through the edit API, so a generated chunk is neither marked modified nor journaled). A pure function of world position; runs on the generation workers.</summary>
internal static class ChunkTerrainFill
{
    /// <summary>
    /// Fills the terrain columns (grass / dirt / stone by depth), lets the cave carvers clear
    /// their air out of the result, floods everything below sea level, then stamps trees onto
    /// the surface. Runs on a generation worker thread.
    /// </summary>
    internal static void GenerateBlockData(Chunk chunk)
    {
        // Worldgen timing for the X/VOXEL_DEBUG console line (WorldgenStats, the `wgen` token)
        var genStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var column = ColumnCache.Get((int)chunk.Position.X, (int)chunk.Position.Z);
        var lowerBoundY = chunk.LowerBoundY;
        chunk.SurfaceMinY = column.MinHeight;

        // Entirely above the terrain: nothing to fill, nothing to carve, nothing to stamp.
        // With trees enabled the band above the surface still needs generation (a tree reaches
        // TreeGenerator.ReachUp above its base), and overhanging limbs near cliffs can cross
        // from higher neighboring columns, so the test uses the tallest column in reach.
        var maxHeight = column.MaxHeight;
        if (WorldGenerationConfig.TreesEnabled)
        {
            var cx = (int)chunk.Position.X;
            var cz = (int)chunk.Position.Z;
            var reach = TreeGenerator.ReachChunkRadius;
            for (var dx = -reach; dx <= reach; dx++)
            for (var dz = -reach; dz <= reach; dz++)
            {
                var neighborMax = ColumnCache.Get(cx + dx, cz + dz).MaxHeight;
                if (neighborMax > maxHeight) maxHeight = neighborMax;
            }
            maxHeight += TreeGenerator.ReachUp + 1;
        }

        // A chunk above the terrain still has water to fill when a body's surface reaches into it:
        // the sea reaches sea level, a lake its own level. A column that holds no water reports
        // NoWater, which no y is at or below.
        var needsWater = WorldGenerationConfig.WaterEnabled
                         && lowerBoundY <= column.MaxWaterLevel;
        if (lowerBoundY > maxHeight + 1 && !needsWater)
        {
            chunk.IsBlockDataReady = true;
            WorldgenStats.AddChunk(System.Diagnostics.Stopwatch.GetTimestamp() - genStart);
            return;
        }

        for (var x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                var i = (x << SizeShift) | z;
                // An oasis column is filled as forest so its ring wears grass (the stored biome
                // stays the desert's; see OasisGenerator)
                var fillBiome = column.Oasis[i] ? Biome.Forest : column.Biomes[i];
                ColumnCache.GenerateColumn(chunk.Blocks, (int)chunk.Position.X, (int)chunk.Position.Z, x, z, column.Heights[i], column.Bed[i], column.DirtDepth[i],
                    lowerBoundY, fillBiome);
            }
        }

        var worldX0 = (int)chunk.Position.X * Size;
        var worldZ0 = (int)chunk.Position.Z * Size;

        // Both carvers only ever clear blocks at or below their own column's terrain top:
        // NoiseCaves breaks each column at `depth < 0` before it reads a block or a sample, and
        // WormCarver skips every y at `depth < margin`, margin = WormSurfaceMargin * (1 - EntranceWeight)
        // in [0, 6] (EntranceWeight is a clamped SmoothStep). Both read this chunk's own
        // column.Heights, so a chunk whose tallest column ends below its floor is untouchable by
        // either and skipping them is bit-identical. It saves the 1000-call cave lattice
        // (NoiseCaves.SampleLattice) on every sea chunk the needsWater clause above keeps alive
        // over its own sea floor.
        var carvable = column.MaxHeight >= lowerBoundY;

        if (carvable && WorldGenerationConfig.NoiseCavesEnabled)
            NoiseCaves.Carve(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        if (carvable && WorldGenerationConfig.WormCavesEnabled)
            WormCarver.Carve(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // After the carvers, so veins rim cave walls instead of being eaten by them.
        if (WorldGenerationConfig.MalachiteEnabled)
            MalachiteGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        // Same pass, same reason; coal's two host rocks let it also fill desert sandstone.
        if (WorldGenerationConfig.CoalEnabled)
            CoalGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        // After the carvers (so caves under the sea floor flood) and before the trees and
        // pebbles, which skip submerged columns rather than growing underwater
        var hasWater = false;
        if (WorldGenerationConfig.WaterEnabled)
        {
            hasWater = FillWater(chunk, column, lowerBoundY);
            StampMud(chunk, column, worldX0, lowerBoundY, worldZ0);
        }

        // After the sea: flooded cells are water, not air, so only dry cave floors pond lava
        if (WorldGenerationConfig.LavaEnabled)
            LavaGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Every species (cacti included) stamps here, wood and leaves with their state bytes.
        // Through a local: a ref to the volatile field is refused, and nothing else can see the
        // chunk until IsBlockDataReady flips below.
        var treeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (WorldGenerationConfig.TreesEnabled)
        {
            var state = chunk.StateArray;
            TreeGenerator.Stamp(chunk.Blocks, ref state, worldX0, lowerBoundY, worldZ0);
            chunk.StateArray = state;
        }
        WorldgenStats.AddTreeTicks(System.Diagnostics.Stopwatch.GetTimestamp() - treeStart);

        // Scenery on the floor, after the trees so it never displaces one and into air only
        if (WorldFeatureConfig.FallenLogsEnabled)
        {
            var state = chunk.StateArray;
            FallenLogGenerator.Stamp(chunk.Blocks, ref state, worldX0, lowerBoundY, worldZ0);
            chunk.StateArray = state;
        }

        if (WorldFeatureConfig.BouldersEnabled)
            BoulderGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        // A landmark on a summit, after everything else so its stack owns the peak
        if (WorldFeatureConfig.CairnsEnabled)
            CairnGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        // Ruins on the dry land, clipped at chunk borders
        if (WorldFeatureConfig.RuinedPillarsEnabled)
            RuinedPillarGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.DesertWellsEnabled)
            DesertWellGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        // Deep rock features, after the carvers so a geode's heart is not carved again
        if (WorldFeatureConfig.GeodesEnabled)
            GeodeGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.MeteorsEnabled)
            MeteorGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Pond plants, after the water: reeds may replace the shallow cell they stand in, and a
        // lily pad floats on the cell above a lake's top water cell
        if (WorldFeatureConfig.ReedsEnabled)
            ReedGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.LilyPadsEnabled)
            LilyPadGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Cave life, after the carvers and the water: only carved, dry air takes any
        if (WorldFeatureConfig.GlowMushroomsEnabled)
            GlowMushroomGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.CaveFormationsEnabled)
            CaveFormationGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Loose rock buried inside stone, after the carvers so a pocket never fills a cave
        if (WorldFeatureConfig.GravelPocketsEnabled)
            GravelPocketGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Underground water, after the lava so a pool never sits in a lava cell
        if (WorldFeatureConfig.UndergroundPoolsEnabled)
            UndergroundPoolGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Frozen-only ice spikes go down before the snow, so a spike keeps its base cell
        if (WorldGenerationConfig.SnowAndIceEnabled)
            IceSpikeGenerator.Stamp(chunk.Blocks, worldX0, lowerBoundY, worldZ0);

        // Last of the cold features: freezes cold surface water to ice, then lays snow on the dry
        // ground and the ice sheet that is left
        if (WorldGenerationConfig.SnowAndIceEnabled)
            SnowGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.PebblesEnabled)
            PebbleGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Before the flowers and foliage, so their per-column scatter fills in around a clump
        // instead of eating into it
        if (WorldFeatureConfig.CottonEnabled)
            CottonGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.FlowersEnabled)
            FlowerGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldFeatureConfig.FoliageEnabled)
            FoliageGenerator.Stamp(chunk.Blocks, column, worldX0, lowerBoundY, worldZ0);

        // Carving removed an unknown number of blocks; a recount is cheaper than bookkeeping.
        // Counting AIR and subtracting beats counting non-air: BlockType.Air is
        // `public const byte Air = 0`, so this binds to the vectorized
        // MemoryExtensions.Count<byte> with no cast, where a "!= Air" predicate would
        // have to run one comparison per cell.
        chunk.SolidCount = BlockCount - chunk.Blocks.AsSpan().Count(BlockType.Air);

        // Only a chunk that actually got flooded can have a frontier to report, plus any
        // chunk low enough to hold lava pockets (the scan itself is fluid-generic)
        if (hasWater || (WorldGenerationConfig.LavaEnabled && lowerBoundY <= WorldGenerationConfig.LavaMaxY))
            ScanFluidFrontier(chunk, column, lowerBoundY);

        chunk.IsBlockDataReady = true;
        WorldgenStats.AddChunk(System.Diagnostics.Stopwatch.GetTimestamp() - genStart);
    }

    /// <summary>
    /// Collects the fluid cells that could already move when this chunk loads into
    /// <see cref="Chunk.FluidFrontier"/>. The sea is generated as static sources, so without this a
    /// cave or an entrance that breached it would leave a wall of water standing beside worldgen
    /// air forever — nothing schedules those cells, since only block edits do.
    ///
    /// Generated water keeps the carved rule: only neighbors at or below their column's natural
    /// terrain top count (see <see cref="FluidWakeRules.IsWakeTarget"/>), since that is what a
    /// carver opened, and open air above the terrain is deliberately excluded so a generated sea
    /// never wakes across a ravine, a beach or a hillside. Minecraft's generated water is static
    /// the same way. A restored chunk can also hold fluid the player poured: a non-source
    /// (mid-flow) cell is always reported, and a source above both its own column's terrain top
    /// and water level — which generated water and lava never are — wakes against any
    /// replaceable neighbor. Generated fluid is always a source at or below one of those two,
    /// so a generated chunk's frontier is exactly the carved rule's.
    ///
    /// +Y is deliberately not tested: every sea-surface cell has air above it and water never
    /// flows upward, so including it would report a useless 256 cells per surface chunk.
    /// Neighbors across a chunk border are left to the main-thread pass, which can wait until
    /// both chunks are loaded, so this only ever reads this chunk's own blocks and stays as
    /// thread-safe as the rest of worldgen.
    /// </summary>
    internal static void ScanFluidFrontier(Chunk chunk, ColumnData column, int lowerBoundY)
    {
        List<ushort>? frontier = null;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var i = Idx(x, y, z);
            if (!BlockType.IsFluid(chunk.Blocks[i])) continue;

            var fluidY = lowerBoundY + y;
            var column2D = (x << SizeShift) | z;
            var fluidHeight = column.Heights[column2D];
            var fluidWaterLevel = column.WaterLevel[column2D];

            var exposed = !FluidState.IsSource(chunk.GetLocalState(x, y, z))
                          || (x > 0 && IsWakeTarget(chunk, column, lowerBoundY, x - 1, y, z, fluidY, fluidHeight, fluidWaterLevel))
                          || (x < SizeMask && IsWakeTarget(chunk, column, lowerBoundY, x + 1, y, z, fluidY, fluidHeight, fluidWaterLevel))
                          || (z > 0 && IsWakeTarget(chunk, column, lowerBoundY, x, y, z - 1, fluidY, fluidHeight, fluidWaterLevel))
                          || (z < SizeMask && IsWakeTarget(chunk, column, lowerBoundY, x, y, z + 1, fluidY, fluidHeight, fluidWaterLevel))
                          || (y > 0 && IsWakeTarget(chunk, column, lowerBoundY, x, y - 1, z, fluidY, fluidHeight, fluidWaterLevel));
            if (!exposed) continue;

            frontier ??= [];
            frontier.Add((ushort)i);
        }

        chunk.FluidFrontier = frontier?.ToArray();
    }

    /// <summary>Whether a chunk-local neighbor is somewhere the fluid beside it may wake into
    /// (<see cref="FluidWakeRules.IsWakeTarget"/> with this chunk's column data).</summary>
    private static bool IsWakeTarget(Chunk chunk, ColumnData column, int lowerBoundY, int x, int y, int z,
        int fluidY, int fluidColumnHeight, int fluidColumnWaterLevel) =>
        FluidWakeRules.IsWakeTarget(chunk.Blocks[Idx(x, y, z)], lowerBoundY + y,
            column.Heights[(x << SizeShift) | z], fluidY, fluidColumnHeight, fluidColumnWaterLevel);

    /// <summary>
    /// Floods every air cell up to each column's own water level (see
    /// <see cref="ColumnData.WaterLevel"/>) in a wet column — an ocean floor, or a lake or river bed
    /// — so oceans, lakes, rivers and sea-floor caves all fill while the dry land basins stay dry.
    /// A lake's level is its own surface, so the pond rests in the ground it was dug into instead of
    /// reaching sea level. Wetness reads the column's <see cref="ColumnData.Bed"/>: a ravine that
    /// merely cut a dry column below its level holds no water, while a ravine under the ocean just
    /// deepens it (the cut is faded out across every other water body, see
    /// <c>TerrainGenerator.RavineCutFade</c>). Generated water is always a source (state 0), which
    /// is what an untouched
    /// state byte reads as, so nothing is written to <c>_state</c> here. A pure per-column function
    /// of world position like the rest of worldgen: neighbors agree at their borders with no shared
    /// state. Returns whether any cell was flooded, which is what decides if this chunk can have a
    /// fluid frontier to report.
    /// </summary>
    private static bool FillWater(Chunk chunk, ColumnData column, int lowerBoundY)
    {
        var filled = false;

        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
        {
            var local = (x << SizeShift) | z;
            var level = column.WaterLevel[local];
            if (column.Bed[local] >= level) continue;

            var top = Math.Min(level - lowerBoundY, Size - 1);
            if (top < 0) continue;

            for (var y = 0; y <= top; y++)
            {
                var i = Idx(x, y, z);
                if (chunk.Blocks[i] != BlockType.Air) continue;
                chunk.Blocks[i] = TerrainGenerator.SeaBlock;
                filled = true;
            }
        }

        return filled;
    }

    /// <summary>
    /// Rots dirt touching the fresh sea to mud on the spot, so worldgen ground spawns muddy
    /// instead of waiting for the fluid tick's watch to convert it. In-chunk neighbors read
    /// straight off this chunk's blocks; across a chunk border the sea is a pure function of
    /// the neighbor column's height, so that is tested analytically. A cave flood breaching
    /// across a border is left to the fluid tick, the same split <see cref="ScanFluidFrontier"/>
    /// uses: worldgen stays a pure per-chunk function with no cross-chunk block reads.
    /// </summary>
    private static void StampMud(Chunk chunk, ColumnData column, int worldX0, int lowerBoundY, int worldZ0)
    {
        // A chunk starting above every level this column can hold water at can neither hold any
        // nor touch any within one cell
        if (lowerBoundY > Math.Max(column.MaxWaterLevel, WorldGenerationConfig.SeaLevel) + 1) return;

        var dirt = TerrainGenerator.SoilBlock;
        var water = TerrainGenerator.SeaBlock;
        var mud = TerrainGenerator.MudBlock;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            if (chunk.Blocks[Idx(x, y, z)] != dirt) continue;
            if (TouchesSeaWater(chunk, x, y, z, worldX0, lowerBoundY, worldZ0, water))
                chunk.Blocks[Idx(x, y, z)] = mud;
        }
    }

    /// <summary>
    /// Whether any of the 26 cells around a chunk-local cell holds water: a direct block read
    /// inside this chunk, or the same <see cref="TerrainGenerator.IsWaterColumn"/> test
    /// <see cref="FillWater"/> is built on outside it. (It was a plain height test before lakes
    /// and rivers, when any below-waterline column held water; that is no longer true.)
    /// </summary>
    private static bool TouchesSeaWater(Chunk chunk, int x, int y, int z, int worldX0, int lowerBoundY, int worldZ0, byte water)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;

            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (nx >= 0 && ny >= 0 && nz >= 0 && nx < Size && ny < Size && nz < Size)
            {
                if (chunk.Blocks[Idx(nx, ny, nz)] == water) return true;
                continue;
            }

            var wx = worldX0 + x + dx;
            var wy = lowerBoundY + y + dy;
            var wz = worldZ0 + z + dz;
            var neighbor = ColumnCache.Get(wx >> SizeShift, wz >> SizeShift);
            var local = ((wx & SizeMask) << SizeShift) | (wz & SizeMask);
            if (wy > neighbor.Heights[local] && wy <= neighbor.WaterLevel[local])
                return true;
        }

        return false;
    }
}
