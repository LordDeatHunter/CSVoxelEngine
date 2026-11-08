using System;
using System.Diagnostics;

namespace VoxelEngine.WorldGen;

/// <summary>
/// A test and probe seam that charges the wall time of <see cref="ChunkTerrainFill.GenerateBlockData"/>
/// to its stages: each <see cref="Mark"/> charges everything since the previous mark (or
/// <see cref="Begin"/>) to the stage just marked, so the stages sum to the whole call. The caller
/// passes it explicitly, so the generation workers never see one; production passes null and pays
/// one null check per stage. Not thread-safe: use one instance per thread. The
/// <see cref="Stage.Water"/> stage is marked only while <see cref="WorldGenerationConfig.WaterEnabled"/>;
/// every other stage is marked once on every filled chunk, an early-out chunk (entirely above
/// the terrain and any water) marks only <see cref="Stage.Columns"/>, one of those in a sky layer's band
/// marks <see cref="Stage.Columns"/>, <see cref="Stage.Clouds"/>, <see cref="Stage.CloudSea"/>,
/// <see cref="Stage.SkyIslands"/>, <see cref="Stage.SkyTrees"/>, <see cref="Stage.RuinsSky"/>, <see cref="Stage.WaystonesSky"/>,
/// <see cref="Stage.SolidCount"/> and <see cref="Stage.FluidFrontier"/>, and a chunk wholly in hell marks
/// only <see cref="Stage.Columns"/>, <see cref="Stage.Hell"/>, <see cref="Stage.HellTrees"/>, <see cref="Stage.RuinsHell"/>,
/// <see cref="Stage.WaystonesHell"/>, <see cref="Stage.SolidCount"/> and <see cref="Stage.FluidFrontier"/>.
/// </summary>
internal sealed class WorldgenStageTimer
{
    /// <summary>The stages of <see cref="ChunkTerrainFill.GenerateBlockData"/>, in production order.</summary>
    internal enum Stage
    {
        /// <summary>The chunk's own ColumnCache.Get, SurfaceMinY, the neighborhood MaxHeight scan (tree and scenery reach) and needsWater.</summary>
        Columns,
        /// <summary>The ColumnCache.GenerateColumn loop.</summary>
        Fill,
        /// <summary>NoiseCaves.Carve.</summary>
        NoiseCaves,
        /// <summary>WormCarver.Carve.</summary>
        WormCaves,
        /// <summary>MalachiteGenerator.Stamp.</summary>
        Malachite,
        /// <summary>CoalGenerator.Stamp.</summary>
        Coal,
        /// <summary>HematiteGenerator.Stamp.</summary>
        Hematite,
        /// <summary>TinGenerator.Stamp.</summary>
        Tin,
        /// <summary>FillWater (marked only while water is enabled).</summary>
        Water,
        /// <summary>StampMud.</summary>
        Mud,
        /// <summary>LavaGenerator.Stamp.</summary>
        Lava,
        /// <summary>TreeGenerator.Stamp.</summary>
        Trees,
        /// <summary>FallenLogGenerator.Stamp.</summary>
        FallenLogs,
        /// <summary>BoulderGenerator.Stamp.</summary>
        Boulders,
        /// <summary>CairnGenerator.Stamp.</summary>
        Cairns,
        /// <summary>RuinedPillarGenerator.Stamp.</summary>
        RuinedPillars,
        /// <summary>DesertWellGenerator.Stamp.</summary>
        DesertWells,
        /// <summary>RuinPlacement.StampSurface.</summary>
        RuinsSurface,
        /// <summary>WaystonePlacement.StampSurface.</summary>
        WaystonesSurface,
        /// <summary>GeodeGenerator.Stamp.</summary>
        Geodes,
        /// <summary>MeteorGenerator.Stamp.</summary>
        Meteors,
        /// <summary>ReedGenerator.Stamp.</summary>
        Reeds,
        /// <summary>LilyPadGenerator.Stamp.</summary>
        LilyPads,
        /// <summary>GlowMushroomGenerator.Stamp.</summary>
        GlowMushrooms,
        /// <summary>CaveFormationGenerator.Stamp.</summary>
        CaveFormations,
        /// <summary>GravelPocketGenerator.Stamp.</summary>
        GravelPockets,
        /// <summary>UndergroundPoolGenerator.Stamp.</summary>
        UndergroundPools,
        /// <summary>RuinPlacement.StampUnderground.</summary>
        RuinsUnderground,
        /// <summary>WaystonePlacement.StampUnderground.</summary>
        WaystonesUnderground,
        /// <summary>IceSpikeGenerator.Stamp.</summary>
        IceSpikes,
        /// <summary>SnowGenerator.StampGround and StampCanopy.</summary>
        Snow,
        /// <summary>PebbleGenerator.Stamp.</summary>
        Pebbles,
        /// <summary>CottonGenerator.Stamp.</summary>
        Cotton,
        /// <summary>FlowerGenerator.Stamp.</summary>
        Flowers,
        /// <summary>FoliageGenerator.Stamp.</summary>
        Foliage,
        /// <summary>TowerPlacement.Stamp, the battle towers.</summary>
        Towers,
        /// <summary>CloudPatchGenerator.Stamp.</summary>
        Clouds,
        /// <summary>CloudSeaGenerator.Stamp.</summary>
        CloudSea,
        /// <summary>SkyIslandGenerator.Stamp.</summary>
        SkyIslands,
        /// <summary>SkyTrees.Stamp (the trees grown on a cache miss included).</summary>
        SkyTrees,
        /// <summary>RuinPlacement.StampSky.</summary>
        RuinsSky,
        /// <summary>WaystonePlacement.StampSky.</summary>
        WaystonesSky,
        /// <summary>BarrierGenerator.Stamp.</summary>
        Barrier,
        /// <summary>HellGenerator.Stamp.</summary>
        Hell,
        /// <summary>HellTrees.Stamp (the trees grown on a cache miss included).</summary>
        HellTrees,
        /// <summary>RuinPlacement.StampHell.</summary>
        RuinsHell,
        /// <summary>WaystonePlacement.StampHell.</summary>
        WaystonesHell,
        /// <summary>The SolidCount recount.</summary>
        SolidCount,
        /// <summary>ScanFluidFrontier.</summary>
        FluidFrontier,
    }

    /// <summary>The number of <see cref="Stage"/> values.</summary>
    internal const int StageCount = (int)Stage.FluidFrontier + 1;

    private readonly long[] _ticks = new long[StageCount];
    private readonly int[] _marks = new int[StageCount];
    private long _last;

    /// <summary>
    /// A test seam called at the end of every <see cref="Mark"/> with the stage just marked, so a
    /// test can snapshot the chunk after each stage. Its own time is charged to the following
    /// stage, which is why the cost probe never sets it.
    /// </summary>
    internal Action<Stage>? AfterMark { get; init; }

    /// <summary>Starts the clock at <paramref name="timestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    internal void Begin(long timestamp) => _last = timestamp;

    /// <summary>Charges the time since the previous mark to <paramref name="stage"/>.</summary>
    internal void Mark(Stage stage)
    {
        var now = Stopwatch.GetTimestamp();
        _ticks[(int)stage] += now - _last;
        _marks[(int)stage]++;
        _last = now;
        AfterMark?.Invoke(stage);
    }

    /// <summary>The <see cref="Stopwatch"/> ticks charged to <paramref name="stage"/> since the last <see cref="Clear"/>.</summary>
    internal long TicksOf(Stage stage) => _ticks[(int)stage];

    /// <summary>How often <paramref name="stage"/> was marked since the last <see cref="Clear"/>.</summary>
    internal int MarksOf(Stage stage) => _marks[(int)stage];

    /// <summary>Zeroes every stage's ticks and marks.</summary>
    internal void Clear()
    {
        Array.Clear(_ticks);
        Array.Clear(_marks);
    }
}
