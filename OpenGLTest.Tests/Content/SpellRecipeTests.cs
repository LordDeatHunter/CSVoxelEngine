using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Spells;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>The rules a <see cref="SpellRecipe"/> compiles by: verdicts, tag allowance, cost and fizzle.</summary>
public class SpellRecipeTests
{
    private static SpellPart Part(SpellPartKind kind, string name, float cost = 0.1f, float complexity = 1f,
        string[]? tags = null, float? damage = null, float? range = null) =>
        SpellPartLoader.Compile(kind, name, new SpellPartDefinition
        {
            CostShare = cost, Complexity = complexity, Tags = tags ?? ["basic"], DamageFactor = damage, RangeFactor = range
        }, "test/" + name + ".json");

    private static SpellPart Element(string name = "fire") => Part(SpellPartKind.Element, name);
    private static SpellPart Shape(string name = "bolt") => Part(SpellPartKind.Shape, name);
    private static SpellPart Modifier(string name) => Part(SpellPartKind.Modifier, name);
    private static SpellPart Trigger(string name) => Part(SpellPartKind.Trigger, name);

    private static SpellVerdict VerdictOf(params SpellPart[] parts) => new SpellRecipe(parts).Compile().Verdict;

    [Fact]
    public void OneElementAndOneShapeIsValid()
    {
        Assert.Equal(SpellVerdict.Valid, VerdictOf(Element(), Shape()));
        Assert.Equal(SpellVerdict.Valid, VerdictOf(Element(), Shape(), Trigger("on_hit"), Modifier("a"), Modifier("b")));
    }

    [Fact]
    public void EachBrokenRuleHasItsVerdict()
    {
        Assert.Equal(SpellVerdict.MissingElement, VerdictOf(Shape()));
        Assert.Equal(SpellVerdict.TooManyElements, VerdictOf(Element("fire"), Element("frost"), Shape()));
        Assert.Equal(SpellVerdict.MissingShape, VerdictOf(Element()));
        Assert.Equal(SpellVerdict.TooManyShapes, VerdictOf(Element(), Shape("bolt"), Shape("aura")));
        Assert.Equal(SpellVerdict.TooManyTriggers, VerdictOf(Element(), Shape(), Trigger("a"), Trigger("b")));
        Assert.Equal(SpellVerdict.DuplicateModifier, VerdictOf(Element(), Shape(), Modifier("power"), Modifier("power")));

        var stats = new SpellRecipe([Element(), Shape(), Part(SpellPartKind.Modifier, "odd", tags: ["advanced"])])
            .Compile(new HashSet<string>(StringComparer.Ordinal) { "basic" });
        Assert.Equal(SpellVerdict.DisallowedPart, stats.Verdict);
        Assert.False(stats.IsValid);
    }

    [Fact]
    public void ModifiersOfTheSameNameButAnotherKindAreNotDuplicates()
    {
        // An element and a modifier may share a file name; only the same modifier key repeats
        Assert.Equal(SpellVerdict.Valid, VerdictOf(Element("power"), Shape(), Modifier("power")));
    }

    [Fact]
    public void APartIsAllowedWhenAnyOfItsTagsIsAllowed()
    {
        var part = Part(SpellPartKind.Modifier, "dual", tags: ["a", "b"]);
        var recipe = new SpellRecipe([Part(SpellPartKind.Element, "e", tags: ["b"]), Part(SpellPartKind.Shape, "s", tags: ["b"]), part]);

        Assert.True(part.IsAllowedBy(new HashSet<string>(StringComparer.Ordinal) { "b" }));
        Assert.False(part.IsAllowedBy(new HashSet<string>(StringComparer.Ordinal) { "c" }));
        Assert.Equal(SpellVerdict.Valid, recipe.Compile(new HashSet<string>(StringComparer.Ordinal) { "b" }).Verdict);
        Assert.Equal(SpellVerdict.Valid, recipe.Compile().Verdict);
    }

    [Fact]
    public void CostRisesStrictlyWithEveryAddedModifier()
    {
        var parts = new List<SpellPart> { Element(), Shape() };
        var previous = new SpellRecipe(parts).Compile().Cost;
        foreach (var name in new[] { "a", "b", "c" })
        {
            parts.Add(Modifier(name));
            var cost = new SpellRecipe(parts).Compile().Cost;
            Assert.True(cost > previous, $"Adding modifier '{name}' did not raise the cost ({previous} -> {cost}).");
            previous = cost;
        }
    }

    [Fact]
    public void CostIsAShareOfTheStartingPool()
    {
        SpellPart[] parts = [Part(SpellPartKind.Element, "e", cost: 0.13f), Part(SpellPartKind.Shape, "s", cost: 0.07f),
            Part(SpellPartKind.Modifier, "m", cost: 0.21f)];
        var stats = new SpellRecipe(parts).Compile();
        Assert.Equal(parts.Sum(p => p.CostShare), stats.Cost / ManaConfig.StartMax, 1e-5f);
    }

    [Fact]
    public void FizzleNeverFallsAndRisesWithComplexity()
    {
        var previous = SpellRecipe.FizzleChanceFor(0f);
        for (var c = 0f; c <= 30f; c += 0.25f)
        {
            var chance = SpellRecipe.FizzleChanceFor(c);
            Assert.InRange(chance, 0f, 1f);
            Assert.True(chance >= previous, $"Fizzle fell at complexity {c}.");
            previous = chance;
        }
        Assert.True(SpellRecipe.FizzleChanceFor(0f) < SpellRecipe.FizzleChanceFor(30f));

        // Through a recipe: a more complex modifier never lowers the chance and eventually raises it
        var last = new SpellRecipe([Element(), Shape(), Part(SpellPartKind.Modifier, "m", complexity: 0f)]).Compile().FizzleChance;
        var rose = false;
        for (var c = 1f; c <= 30f; c += 1f)
        {
            var chance = new SpellRecipe([Element(), Shape(), Part(SpellPartKind.Modifier, "m", complexity: c)]).Compile().FizzleChance;
            Assert.True(chance >= last);
            rose |= chance > last;
            last = chance;
        }
        Assert.True(rose);
    }

    [Fact]
    public void ModifierFactorsMultiplyIntoTheStats()
    {
        var plain = new SpellRecipe([Element(), Shape()]).Compile();
        var powered = new SpellRecipe([Element(), Shape(), Part(SpellPartKind.Modifier, "p", damage: 1.5f)]).Compile();
        var ranged = new SpellRecipe([Element(), Shape(), Part(SpellPartKind.Modifier, "r", range: 1.75f)]).Compile();

        Assert.True(powered.DamageFactor > plain.DamageFactor);
        Assert.Equal(plain.RangeFactor, powered.RangeFactor);
        Assert.True(ranged.RangeFactor > plain.RangeFactor);
        Assert.Equal(plain.DamageFactor, ranged.DamageFactor);
    }

    [Fact]
    public void PartOrderDoesNotChangeTheStats()
    {
        SpellPart[] parts = [Element(), Shape(), Part(SpellPartKind.Modifier, "p", cost: 0.2f, complexity: 2f, damage: 1.5f),
            Part(SpellPartKind.Modifier, "r", cost: 0.05f, complexity: 3f, range: 2f), Trigger("t")];
        var forward = new SpellRecipe(parts).Compile();
        var backward = new SpellRecipe(Enumerable.Reverse(parts)).Compile();
        Assert.Equal(forward.Verdict, backward.Verdict);
        Assert.Equal(forward.Cost, backward.Cost, 1e-4f);
        Assert.Equal(forward.Complexity, backward.Complexity, 1e-5f);
        Assert.Equal(forward.FizzleChance, backward.FizzleChance, 1e-5f);
        Assert.Equal(forward.DamageFactor, backward.DamageFactor, 1e-5f);
        Assert.Equal(forward.RangeFactor, backward.RangeFactor, 1e-5f);
    }

    /// <summary>
    /// Encodes the <see cref="ManaConfig"/> design rule that a typical spell costs 20-40% of the starting
    /// pool. When a later part (MG-13) is legitimately outside it, revisit the rule here rather than
    /// loosening the band silently.
    /// </summary>
    [Fact]
    public void EveryValidShippedSpellCostsAShareTheDesignAllows()
    {
        TestContent.EnsureLoaded();
        var elements = SpellPartRegistry.All.Where(p => p.Kind == SpellPartKind.Element).ToArray();
        var shapes = SpellPartRegistry.All.Where(p => p.Kind == SpellPartKind.Shape).ToArray();
        var modifiers = SpellPartRegistry.All.Where(p => p.Kind == SpellPartKind.Modifier).ToArray();
        Assert.NotEmpty(elements);
        Assert.NotEmpty(shapes);

        foreach (var element in elements)
        foreach (var shape in shapes)
        for (var mask = 0; mask < 1 << modifiers.Length; mask++)
        {
            var parts = new List<SpellPart> { element, shape };
            for (var i = 0; i < modifiers.Length; i++)
                if ((mask & (1 << i)) != 0) parts.Add(modifiers[i]);

            var stats = new SpellRecipe(parts).Compile();
            var label = string.Join(" + ", parts.Select(p => p.Key));
            Assert.True(stats.IsValid, $"{label} is {stats.Verdict}.");
            Assert.InRange(stats.Cost / ManaConfig.StartMax, 0.2f, 0.4f);
        }
    }
}
