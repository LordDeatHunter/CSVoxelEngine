using System;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of <see cref="TreeSpeciesCompiler.Compile"/>: what a species definition must say, what
/// defaults apply, and that every rejection names the file it came from. Real block ids feed the
/// resolver so the fluid rule sees a real fluid.
/// </summary>
public sealed class TreeSpeciesCompilerTests
{
    private const string FakePath = "data/trees/x.json";

    private readonly byte _woodId;
    private readonly byte _leafId;
    private readonly byte _waterId;

    public TreeSpeciesCompilerTests()
    {
        TestContent.EnsureLoaded();
        _woodId = ContentRegistry.RequireBlock("oak_log").Id;
        _leafId = ContentRegistry.RequireBlock("oak_leaves").Id;
        _waterId = ContentRegistry.RequireBlock("water").Id;
    }

    private byte? Resolve(string n) => n switch
    {
        "block/wood" => _woodId,
        "block/leaf" => _leafId,
        "block/water" => _waterId,
        _ => null,
    };

    private static TreeSpeciesDefinition Valid() => new()
    {
        Wood = "block/wood",
        Leaves = "block/leaf",
        Growth = new()
        {
            Logic = GrowthLogicRegistry.ByName.Keys.First(),
            SignalEnergy = 10,
            UpProbability = 2,
            LowestBranchHeight = 3,
            MaturitySteps = [20, 40],
        },
        Thickness = new() { TipPx = 1, PipeExponent = 2f, MaxPx = 16 },
        LeafCluster = new() { Radius = 2, Hydration = 3, TipMaxPx = 3 },
        Features = [new() { Type = "roots", Depth = 3 }],
        Lod = new() { CrownHeight = [4, 8], CrownRadius = 2 },
    };

    private static TreeSpeciesDefinition.WorldgenDefinition ValidWorldgen() => new()
    {
        Biomes = [nameof(Biome.Forest)],
        Density = 0.5f,
        Spacing = 1,
        MaxSlope = 1,
        MinGround = 0,
        MaxGround = 10,
    };

    private TreeSpecies Compile(TreeSpeciesDefinition dto) =>
        TreeSpeciesCompiler.Compile("x", dto, FakePath, Resolve);

    private void Rejects(Action<TreeSpeciesDefinition> mutate)
    {
        var dto = Valid();
        mutate(dto);
        var e = Assert.Throws<InvalidOperationException>(() => Compile(dto));
        Assert.Contains(FakePath, e.Message);
    }

    [Fact]
    public void CompilesAValidDefinition()
    {
        var dto = Valid();
        var species = Compile(dto);
        Assert.Equal("x", species.Name);
        Assert.Equal(Resolve(dto.Wood!), species.WoodId);
        Assert.Equal(Resolve(dto.Leaves!), species.LeavesId);
        Assert.True(species.HasLeaves);
        Assert.Equal(dto.Growth!.Logic, species.Logic.Name);
        Assert.Same(GrowthLogicRegistry.Require(dto.Growth.Logic!), species.Logic);
        Assert.Equal(dto.Growth.MaturitySteps![0], species.MaturityStepsMin);
        Assert.Equal(dto.Growth.MaturitySteps[1], species.MaturityStepsMax);
        Assert.Equal(dto.Features!.Single().Type, species.Features.Single().Feature.Name);
        Assert.Equal(dto.Features.Single().Depth, species.Features.Single().Depth);

        // Omitted optional fields take their defaults.
        Assert.Null(species.Worldgen);
        Assert.Equal([(0f, 1f)], species.GirthProfile);
        Assert.Equal(TrunkShape.Circle, species.TrunkShape);
        Assert.Equal(TrunkRim.Block, species.Rim);
        Assert.Equal(LeafClusterShape.Blob, species.LeafShape);
        Assert.Equal(LodCrownProfile.Dome, species.LodProfile);

        // Enum strings parse case-insensitively; worldgen biomes come through in order.
        dto.Thickness!.TrunkShape = "SQUARE";
        dto.Thickness.Rim = "Pixel";
        dto.Thickness.GirthProfile = [[0f, 1.5f], [0.5f, 1f]];
        dto.LeafCluster!.Shape = "cOnE";
        dto.Lod!.Profile = "COLUMN";
        dto.Worldgen = ValidWorldgen();
        dto.Worldgen.Biomes = ["TAIGA", "frozen"];
        species = Compile(dto);
        Assert.Equal(TrunkShape.Square, species.TrunkShape);
        Assert.Equal(TrunkRim.Pixel, species.Rim);
        Assert.Equal(LeafClusterShape.Cone, species.LeafShape);
        Assert.Equal(LodCrownProfile.Column, species.LodProfile);
        Assert.Equal([(0f, 1.5f), (0.5f, 1f)], species.GirthProfile);
        Assert.NotNull(species.Worldgen);
        Assert.Equal([Biome.Taiga, Biome.Frozen], species.Worldgen!.Biomes);
        Assert.True(species.Worldgen.GrowsIn(Biome.Taiga));
        Assert.False(species.Worldgen.GrowsIn(Biome.Forest));
    }

    [Fact]
    public void ALeaflessSpeciesCompilesWithoutACluster()
    {
        var dto = Valid();
        dto.Leaves = null;
        dto.LeafCluster = null;
        var species = Compile(dto);
        Assert.False(species.HasLeaves);
    }

    [Fact]
    public void RejectsBadBlockReferences()
    {
        Rejects(d => d.Wood = null);
        Rejects(d => d.Wood = "block/unknown");
        Rejects(d => d.Wood = "block/water");
        Rejects(d => d.Leaves = "block/unknown");
        Rejects(d => d.Leaves = "block/water");
        Rejects(d => d.Leaves = d.Wood);
    }

    [Fact]
    public void RejectsBadGrowth()
    {
        Rejects(d => d.Growth = null);
        Rejects(d => d.Growth!.Logic = null);
        Rejects(d => d.Growth!.Logic = "no_such_logic");
        Rejects(d => d.Growth!.SignalEnergy = 0);
        Rejects(d => d.Growth!.UpProbability = -1);
        Rejects(d => d.Growth!.LowestBranchHeight = -1);
        Rejects(d => d.Growth!.MaturitySteps = null);
        Rejects(d => d.Growth!.MaturitySteps = [5, 4]);
        Rejects(d => d.Growth!.MaturitySteps = [0, 4]);
        Rejects(d => d.Growth!.MaturitySteps = [1, 2, 3]);
    }

    [Fact]
    public void RejectsBadThickness()
    {
        Rejects(d => d.Thickness = null);
        Rejects(d => d.Thickness!.TipPx = 0);
        Rejects(d => d.Thickness!.TipPx = WoodState.PixelsPerBlock + 1);
        Rejects(d => d.Thickness!.MaxPx = d.Thickness.TipPx - 1);
        Rejects(d => d.Thickness!.PipeExponent = 0f);
        Rejects(d => d.Thickness!.PipeExponent = float.NaN);
        Rejects(d => d.Thickness!.GirthProfile = [[0.1f, 1f]]);
        Rejects(d => d.Thickness!.GirthProfile = [[0f, 1f], [0.5f, 1f], [0.5f, 1f]]);
        Rejects(d => d.Thickness!.GirthProfile = [[0f, 1f], [0.6f, 1f], [0.4f, 1f]]);
        Rejects(d => d.Thickness!.GirthProfile = [[0f, 1f], [1.5f, 1f]]);
        Rejects(d => d.Thickness!.GirthProfile = [[0f, 0f]]);
        Rejects(d => d.Thickness!.GirthProfile = [[0f]]);
        Rejects(d => d.Thickness!.TrunkShape = "hexagon");
        Rejects(d => d.Thickness!.Rim = "1");
    }

    [Fact]
    public void LeafClusterAndLeavesMustAgree()
    {
        Rejects(d => d.Leaves = null);
        Rejects(d => d.LeafCluster = null);
        Rejects(d => d.LeafCluster!.Hydration = 0);
        Rejects(d => d.LeafCluster!.Hydration = LeafState.MaxHydration + 1);
        Rejects(d => d.LeafCluster!.Radius = 0);
        Rejects(d => d.LeafCluster!.TipMaxPx = d.Thickness!.MaxPx + 1);
        Rejects(d => d.LeafCluster!.Shape = "cube");

        // The bounds themselves are legal.
        var dto = Valid();
        dto.LeafCluster!.Hydration = LeafState.MaxHydration;
        dto.LeafCluster.TipMaxPx = dto.Thickness!.MaxPx;
        Assert.Equal(LeafState.MaxHydration, Compile(dto).LeafHydration);
    }

    [Fact]
    public void RejectsBadFeatures()
    {
        Rejects(d => d.Features = [new() { Type = "no_such_feature" }]);
        Rejects(d => d.Features = [new() { Type = null }]);
        Rejects(d => d.Features = [new() { Type = "roots", Depth = 0 }]);
        Rejects(d => d.Features = [new() { Type = "roots" }]);
        Rejects(d => d.Features = [new() { Type = "buttress", Depth = 2 }]);
        Rejects(d => d.Features = [new() { Type = "roots", Depth = 2 }, new() { Type = "roots", Depth = 3 }]);

        // Omitted features compile to none.
        var dto = Valid();
        dto.Features = null;
        Assert.Empty(Compile(dto).Features);
    }

    [Fact]
    public void RejectsBadLod()
    {
        Rejects(d => d.Lod = null);
        Rejects(d => d.Lod!.CrownHeight = [8, 4]);
        Rejects(d => d.Lod!.CrownHeight = [-1, 4]);
        Rejects(d => d.Lod!.CrownHeight = [4]);
        Rejects(d => d.Lod!.CrownRadius = -1);
        Rejects(d => d.Lod!.CrownRadius = null);
        Rejects(d => d.Lod!.Profile = "sphere");
    }

    [Fact]
    public void RejectsBadWorldgen()
    {
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Biomes = []; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Biomes = ["jungle"]; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Biomes = ["0"]; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Biomes = ["forest", "FOREST"]; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Density = 1.5f; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Density = -0.1f; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Spacing = -1; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.MaxSlope = -1; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.MinSoil = -1; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.MinGround = d.Worldgen.MaxGround + 1; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.MaxGround = null; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Openness = 1.5f; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Openness = -0.1f; });
        Rejects(d => { d.Worldgen = ValidWorldgen(); d.Worldgen.Openness = float.NaN; });
    }

    [Fact]
    public void EveryRejectionNamesTheFile()
    {
        // A different path must show up in the message, not a hardcoded one.
        const string other = "elsewhere/trees/y.json";
        var dto = Valid();
        dto.Wood = "block/unknown";
        var e = Assert.Throws<InvalidOperationException>(
            () => TreeSpeciesCompiler.Compile("y", dto, other, Resolve));
        Assert.Contains(other, e.Message);
        Assert.DoesNotContain(FakePath, e.Message);
    }

    [Fact]
    public void OmittedOpennessAndSpawnClearMeanFullThinningAndAClearedSpawn()
    {
        var omitted = Valid();
        omitted.Worldgen = ValidWorldgen();
        var explicitDefaults = Valid();
        explicitDefaults.Worldgen = ValidWorldgen();
        explicitDefaults.Worldgen.Openness = 1f;
        explicitDefaults.Worldgen.SpawnClear = true;
        var opposite = Valid();
        opposite.Worldgen = ValidWorldgen();
        opposite.Worldgen.Openness = 0f;
        opposite.Worldgen.SpawnClear = false;

        var a = Compile(omitted).Worldgen!;
        var b = Compile(explicitDefaults).Worldgen!;
        var c = Compile(opposite).Worldgen!;
        Assert.Equal(b.Openness, a.Openness);
        Assert.Equal(b.SpawnClear, a.SpawnClear);
        // The explicit values are really read, so the equality above is not two ignored fields
        Assert.NotEqual(a.Openness, c.Openness);
        Assert.NotEqual(a.SpawnClear, c.SpawnClear);
    }
}
