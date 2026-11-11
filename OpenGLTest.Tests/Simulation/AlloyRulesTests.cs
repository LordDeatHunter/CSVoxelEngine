using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// The pure crucible rules (<see cref="AlloyRules"/>) over a synthetic alloy, so they hold
/// whatever the shipped recipes say: a plain kiln keeps a vessel to one metal, an alloy kiln lets
/// one alloy's metals share it, a full set becomes the alloy without gaining or losing a
/// milliliter, what does not make a set waits, and the result never depends on the order the
/// metals went in.
/// </summary>
public sealed class AlloyRulesTests
{
    private const byte A = 1;
    private const byte B = 2;
    private const byte Alloy = 3;
    private const byte Other = 4;
    private const int PartA = 600;
    private const int PartB = 200;

    private static readonly IReadOnlyList<AlloyRecipe> Alloys =
        [new AlloyRecipe("alloy", Alloy, [(A, PartA), (B, PartB)])];

    private static CrucibleComponent Pending(params (byte Material, int Ml)[] metals) =>
        new(metals.Select(m => new CrucibleMetal(m.Material, 0, m.Ml)));

    [Fact]
    public void APlainKilnKeepsAVesselToOneMetal()
    {
        Assert.True(AlloyRules.CanMix(default, A, canAlloy: false, Alloys));
        Assert.True(AlloyRules.CanMix(Pending((A, 100)), A, canAlloy: false, Alloys));
        Assert.False(AlloyRules.CanMix(Pending((A, 100)), B, canAlloy: false, Alloys));
        // Molten counts as much as pending does
        Assert.False(AlloyRules.CanMix(new CrucibleComponent(A, 100, 0), B, canAlloy: false, Alloys));
        // A vessel already mixed elsewhere takes nothing more in a plain kiln, not even its own metals
        Assert.False(AlloyRules.CanMix(Pending((A, 100), (B, 100)), A, canAlloy: false, Alloys));
    }

    [Fact]
    public void AnAlloyKilnMixesOnlyTheMetalsOfOneAlloy()
    {
        Assert.True(AlloyRules.CanMix(Pending((A, 100)), B, canAlloy: true, Alloys));
        Assert.True(AlloyRules.CanMix(Pending((B, 100)), A, canAlloy: true, Alloys));
        Assert.True(AlloyRules.CanMix(Pending((Alloy, 800), (A, 100)), B, canAlloy: true, Alloys));
        Assert.False(AlloyRules.CanMix(Pending((A, 100)), Other, canAlloy: true, Alloys));
        Assert.False(AlloyRules.CanMix(Pending((Other, 100)), A, canAlloy: true, Alloys));
        // An unrelated metal still melts alone in an alloy kiln
        Assert.True(AlloyRules.CanMix(Pending((Other, 100)), Other, canAlloy: true, Alloys));
    }

    [Theory]
    [InlineData(600, 200, 0, 0)]
    [InlineData(1300, 450, 0, 0)]
    [InlineData(300, 300, 400, 0)]
    [InlineData(100, 900, 500, 50)]
    [InlineData(599, 200, 0, 0)]
    public void ResolveConservesEveryMilliliterAndLeavesNoFullSet(int pendingA, int pendingB, int moltenA,
        int moltenB)
    {
        var before = new CrucibleComponent([new CrucibleMetal(A, moltenA, pendingA), new CrucibleMetal(B, moltenB, pendingB)]);
        var after = AlloyRules.Resolve(before, Alloys);

        Assert.Equal(before.TotalMl, after.TotalMl);
        // The parts that went into the alloy are exactly the alloy that came out, in the recipe's ratio
        var usedA = before.Metals.Where(m => m.MaterialId == A).Sum(m => m.TotalMl) - TotalOf(after, A);
        var usedB = before.Metals.Where(m => m.MaterialId == B).Sum(m => m.TotalMl) - TotalOf(after, B);
        Assert.Equal(usedA + usedB, TotalOf(after, Alloy));
        Assert.Equal(usedA * PartB, usedB * PartA);
        // What is left cannot make another set
        Assert.True(TotalOf(after, A) < PartA || TotalOf(after, B) < PartB);
        // A second pass changes nothing
        Assert.Equal(after, AlloyRules.Resolve(after, Alloys));
        // The alloy waits as charge, to melt at the kiln's pace
        Assert.Equal(0, after.MoltenOf(Alloy));
    }

    [Fact]
    public void PartsWaitForTheirSetWhileAnyOtherMetalMelts()
    {
        // Both parts present but short of a set: neither melts in an alloy kiln...
        var incomplete = Pending((A, PartA), (B, PartB - 1));
        Assert.Equal(0, AlloyRules.NextToMelt(incomplete, canAlloy: true, Alloys));
        // ...while a plain kiln melts what it holds
        Assert.NotEqual(0, AlloyRules.NextToMelt(incomplete, canAlloy: false, Alloys));

        // A lone part melts like in a plain kiln, and the alloy melts while the surplus waits
        Assert.Equal(A, AlloyRules.NextToMelt(Pending((A, 100)), canAlloy: true, Alloys));
        var resolved = AlloyRules.Resolve(Pending((A, PartA + 100), (B, PartB + 50)), Alloys);
        Assert.Equal(Alloy, AlloyRules.NextToMelt(resolved, canAlloy: true, Alloys));
        Assert.True(resolved.PendingOf(A) > 0 && resolved.PendingOf(B) > 0);
    }

    [Fact]
    public void TheOrderTheMetalsWentInChangesNothing()
    {
        var charges = new (byte Material, int Ml)[] { (A, 200), (A, 200), (B, 100), (A, 200), (B, 100), (B, 100) };
        var results = new[] { charges, charges.Reverse().ToArray(), charges.OrderBy(c => c.Material).ToArray() }
            .Select(order => AlloyRules.Resolve(
                order.Aggregate(default(CrucibleComponent), (c, charge) => c.With(charge.Material, 0, charge.Ml)), Alloys))
            .ToArray();
        Assert.All(results, r => Assert.Equal(results[0], r));
    }

    private static int TotalOf(CrucibleComponent contents, byte material) =>
        contents.MoltenOf(material) + contents.PendingOf(material);
}
