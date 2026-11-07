using System;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// A kiln anchor's <c>kiln</c> object as block data (<see cref="KilnProfile"/>): omitted fields
/// are the plain kiln's, and a profile the kiln manager could not run, or a kiln object on a
/// block that is not a multiblock anchor, is refused naming the file.
/// </summary>
public sealed class KilnProfileTests
{
    private const string Path = "data/blocks/x.json";

    public KilnProfileTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AnEmptyKilnObjectIsThePlainKilnAndEachFieldOverridesOnlyItself()
    {
        Assert.Null(BlockSchemaParser.ParseKiln(null, Path));
        Assert.Equal(KilnProfile.Default, BlockSchemaParser.ParseKiln(new BlockDefinition.KilnDefinition(), Path));

        var alloying = BlockSchemaParser.ParseKiln(new BlockDefinition.KilnDefinition { CanAlloy = true }, Path)!;
        Assert.True(alloying.CanAlloy);
        Assert.Equal(KilnProfile.Default with { CanAlloy = true }, alloying);

        var bigHearth = BlockSchemaParser.ParseKiln(
            new BlockDefinition.KilnDefinition { FuelCap = KilnProfile.Default.FuelCap * 2 }, Path)!;
        Assert.Equal(2 * KilnProfile.Default.FuelCap, bigHearth.FuelCap);
        Assert.Equal(KilnProfile.Default.MeltSpeed, bigHearth.MeltSpeed);
    }

    [Fact]
    public void TheRegistryRefusesAKilnItCannotRunNamingTheFile()
    {
        foreach (var definition in new[]
                 {
                     new BlockDefinition.KilnDefinition { FuelCap = KilnProfile.MinFuelCap - 1 },
                     new BlockDefinition.KilnDefinition { FuelCap = KilnProfile.MaxFuelCap + 1 },
                     new BlockDefinition.KilnDefinition { MeltSpeed = 0f },
                     new BlockDefinition.KilnDefinition { MeltSpeed = -1f },
                     new BlockDefinition.KilnDefinition { MeltSpeed = KilnProfile.MaxMeltSpeed * 1.01f },
                     new BlockDefinition.KilnDefinition { MeltSpeed = float.NaN },
                     new BlockDefinition.KilnDefinition { MeltSpeed = float.PositiveInfinity },
                 })
            Assert.Contains(Path, Assert.Throws<InvalidOperationException>(() =>
                BlockSchemaParser.ParseKiln(definition, Path)).Message);

        // The bounds themselves are legal
        Assert.NotNull(BlockSchemaParser.ParseKiln(new BlockDefinition.KilnDefinition
            { FuelCap = KilnProfile.MinFuelCap, MeltSpeed = KilnProfile.MaxMeltSpeed }, Path));
        Assert.NotNull(BlockSchemaParser.ParseKiln(new BlockDefinition.KilnDefinition
            { FuelCap = KilnProfile.MaxFuelCap }, Path));
    }

    [Fact]
    public void OnlyAMultiblockAnchorMayBeAKiln()
    {
        var plain = new BlockDefinition { Kiln = new BlockDefinition.KilnDefinition() };
        Assert.Contains(Path, Assert.Throws<InvalidOperationException>(() =>
            BlockFieldRules.ValidateMultiblock(plain, BlockRenderer.Model, Path)).Message);
    }

    [Fact]
    public void AKilnTakesTheLayoutOfItsSizeAndASizeWithNoKilnModelIsRefused()
    {
        foreach (var layout in KilnLayout.All)
            Assert.Same(layout, BlockSchemaParser.ParseKiln(new BlockDefinition.KilnDefinition(), Path,
                layout.VolumeSize)!.Layout);

        for (var size = MultiblockShape.MinSize; size <= MultiblockShape.MaxSize; size++)
        {
            if (KilnLayout.ForSize(size) != null) continue;
            Assert.Contains(Path, Assert.Throws<InvalidOperationException>(() =>
                BlockSchemaParser.ParseKiln(new BlockDefinition.KilnDefinition(), Path, size)).Message);
        }
    }

    [Fact]
    public void TheAlloyFurnaceOutdoesThePlainKilnAndIsTheOneThatAlloys()
    {
        var kiln = ContentRegistry.RequireBlock("mud_brick_kiln").Kiln!;
        var furnace = ContentRegistry.RequireBlock("alloy_furnace").Kiln!;

        Assert.True(furnace.FuelCap > kiln.FuelCap);
        Assert.True(furnace.MeltSeconds < kiln.MeltSeconds);
        Assert.True(furnace.CanAlloy);
        Assert.False(kiln.CanAlloy);
    }
}
