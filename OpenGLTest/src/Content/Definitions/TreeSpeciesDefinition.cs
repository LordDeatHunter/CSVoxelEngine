namespace silkgltriangle;

/// <summary>
/// JSON shape of <c>assets/data/trees/&lt;species&gt;.json</c>: one tree species, the wood and
/// leaves it grows, how it grows, how thick it gets, how its leaves cluster, the post-growth
/// features, its far-LOD crown and (optionally) where worldgen plants it. Every property is
/// nullable so <see cref="TreeSpeciesCompiler"/> can tell "not written" from a written value and
/// apply defaults or report the missing field, naming the file.
/// </summary>
public sealed class TreeSpeciesDefinition
{
    /// <summary>The wood block the species grows (<c>"block/log"</c> or a bare block name). Required.</summary>
    public string? Wood { get; set; }

    /// <summary>The leaves block, or omitted for a leafless species (cactus). When present,
    /// <see cref="LeafCluster"/> is required; when absent, it must be absent too.</summary>
    public string? Leaves { get; set; }

    /// <summary>How the tree grows. Required.</summary>
    public GrowthDefinition? Growth { get; set; }

    /// <summary>Branch and trunk thickness. Required.</summary>
    public ThicknessDefinition? Thickness { get; set; }

    /// <summary>The leaf cluster grown at branch tips; required exactly when <see cref="Leaves"/> is set.</summary>
    public LeafClusterDefinition? LeafCluster { get; set; }

    /// <summary>Post-growth stages (roots, buttress); omitted means none.</summary>
    public FeatureDefinition[]? Features { get; set; }

    /// <summary>The far-LOD crown approximation. Required.</summary>
    public LodDefinition? Lod { get; set; }

    /// <summary>Worldgen placement; omitted means the species never generates naturally.</summary>
    public WorldgenDefinition? Worldgen { get; set; }

    /// <summary>The <c>growth</c> object.</summary>
    public sealed class GrowthDefinition
    {
        /// <summary>Name of a registered <see cref="IGrowthLogic"/>. Required.</summary>
        public string? Logic { get; set; }

        /// <summary>Energy a growth signal starts with, at least 1. Required.</summary>
        public int? SignalEnergy { get; set; }

        /// <summary>Weight of growing straight up against the sideways choices, at least 0. Required.</summary>
        public int? UpProbability { get; set; }

        /// <summary>Height above the root below which no branch forks, at least 0. Required.</summary>
        public int? LowestBranchHeight { get; set; }

        /// <summary>Two ints <c>[min, max]</c> with 1 &lt;= min &lt;= max: growth steps to maturity. Required.</summary>
        public int[]? MaturitySteps { get; set; }
    }

    /// <summary>The <c>thickness</c> object.</summary>
    public sealed class ThicknessDefinition
    {
        /// <summary>Diameter of a branch tip in pixels, 1..16. Required.</summary>
        public int? TipPx { get; set; }

        /// <summary>Exponent of the pipe model combining child diameters, finite and &gt; 0. Required.</summary>
        public float? PipeExponent { get; set; }

        /// <summary>Largest diameter in pixels, tipPx..128 (wide trunks span several blocks). Required.</summary>
        public int? MaxPx { get; set; }

        /// <summary>
        /// <c>[[height01, multiplier], ...]</c>: girth multiplier along the trunk, heights strictly
        /// increasing from exactly 0 and within 0..1, multipliers &gt; 0. Omitted means <c>[[0, 1]]</c>.
        /// </summary>
        public float[][]? GirthProfile { get; set; }

        /// <summary><c>circle</c> (default) or <c>square</c>; see <see cref="silkgltriangle.TrunkShape"/>.</summary>
        public string? TrunkShape { get; set; }

        /// <summary><c>block</c> (default) or <c>pixel</c>; see <see cref="TrunkRim"/>.</summary>
        public string? Rim { get; set; }
    }

    /// <summary>The <c>leafCluster</c> object.</summary>
    public sealed class LeafClusterDefinition
    {
        /// <summary><c>blob</c> (default), <c>cone</c> or <c>pad</c>; see <see cref="LeafClusterShape"/>.</summary>
        public string? Shape { get; set; }

        /// <summary>Cluster radius in blocks, 1..8. Required.</summary>
        public int? Radius { get; set; }

        /// <summary>Hydration a fresh leaf starts with, 1..<see cref="LeafState.MaxHydration"/>. Required.</summary>
        public int? Hydration { get; set; }

        /// <summary>Largest branch diameter (pixels) that still grows a cluster, 1..maxPx. Required.</summary>
        public int? TipMaxPx { get; set; }

        /// <summary>
        /// Cluster radius of a tree whose root is 1 px thick, 1..radius. The radius grows with the
        /// root diameter from here to <see cref="Radius"/> at <see cref="FullRadiusPx"/>. Defaults to
        /// <see cref="Radius"/>, which means the crown never scales.
        /// </summary>
        public int? MinRadius { get; set; }

        /// <summary>
        /// Root diameter (pixels) at which the cluster reaches its full <see cref="Radius"/>,
        /// 1..min(maxPx, 16): the world stores at most a full cube per wood cell. Defaults to 16
        /// (<see cref="WoodState.PixelsPerBlock"/>).
        /// </summary>
        public int? FullRadiusPx { get; set; }
    }

    /// <summary>One entry of the <c>features</c> array.</summary>
    public sealed class FeatureDefinition
    {
        /// <summary>Name of a registered <see cref="ITreeFeature"/>. Required; at most once per species.</summary>
        public string? Type { get; set; }

        /// <summary>Feature depth in blocks; default 0. <c>roots</c> needs at least 1, <c>buttress</c> takes 0.</summary>
        public int? Depth { get; set; }
    }

    /// <summary>The <c>lod</c> object.</summary>
    public sealed class LodDefinition
    {
        /// <summary>Two ints <c>[min, max]</c> with 0 &lt;= min &lt;= max: crown height range. Required.</summary>
        public int[]? CrownHeight { get; set; }

        /// <summary>Crown radius in blocks, at least 0. Required.</summary>
        public int? CrownRadius { get; set; }

        /// <summary><c>dome</c> (default), <c>cone</c> or <c>column</c>; see <see cref="LodCrownProfile"/>.</summary>
        public string? Profile { get; set; }
    }

    /// <summary>The <c>worldgen</c> object.</summary>
    public sealed class WorldgenDefinition
    {
        /// <summary><see cref="Biome"/> names the species grows in; non-empty, no duplicates. Required.</summary>
        public string[]? Biomes { get; set; }

        /// <summary>Chance per candidate column, 0..1. Required.</summary>
        public float? Density { get; set; }

        /// <summary>Whether the species is planted only inside an oasis disc (an oasis palm), the
        /// biomes list then only naming the climate it belongs to. Default false.</summary>
        public bool? Oasis { get; set; }

        /// <summary>Minimum gap to another trunk in blocks, at least 0. Required.</summary>
        public int? Spacing { get; set; }

        /// <summary>Largest height step across the footprint, at least 0. Required.</summary>
        public int? MaxSlope { get; set; }

        /// <summary>Soil depth the column needs, at least 0; default 0.</summary>
        public int? MinSoil { get; set; }

        /// <summary>Lowest ground height the species grows on. Required, &lt;= <see cref="MaxGround"/>.</summary>
        public int? MinGround { get; set; }

        /// <summary>Highest ground height the species grows on. Required.</summary>
        public int? MaxGround { get; set; }

        /// <summary>Share of the forest-openness thinning applied, 0..1; default 1.</summary>
        public float? Openness { get; set; }

        /// <summary>Whether the species keeps out of the spawn clearing; default true.</summary>
        public bool? SpawnClear { get; set; }
    }
}
