using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Recipes;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// Alloy recipes (<c>data/recipes/alloy</c>) as data: a set yields exactly the sum of its parts,
/// and the loader refuses a recipe a crucible could not resolve unambiguously (a lone part, a
/// part that is the result, a repeated part, a metal that is a part of two alloys, an unknown
/// material, an amount out of range).
/// </summary>
public sealed class AlloyRecipeTests
{
    private const string Path = "recipes/alloy/test.json";

    public AlloyRecipeTests() => TestContent.EnsureLoaded();

    private static AlloyRecipeDefinition Definition(string? result, params (string? Fluid, int? Ml)[] parts) => new()
    {
        Result = result,
        Parts = parts.Select(p => new AlloyRecipeDefinition.PartDefinition { Fluid = p.Fluid, Ml = p.Ml }).ToList(),
    };

    // Two molten materials no shipped alloy uses as a part, and one that is a part
    private static (string Free1, string Free2, string Part) Names()
    {
        var part = ContentRegistry.MoltenMaterialById(ContentRegistry.AlloyRecipes[0].Parts[0].MaterialId)!.Name;
        var free = ContentRegistry.MoltenMaterials
            .Where(m => !ContentRegistry.AlloyRecipes.Any(a => a.HasPart(m.Id)))
            .Select(m => m.Name).ToArray();
        Assert.True(free.Length >= 2, "the test needs two molten materials outside every alloy's parts");
        return (free[0], free[1], part);
    }

    [Fact]
    public void EveryShippedAlloyYieldsTheSumOfItsDistinctPartsAndShareNoPart()
    {
        Assert.NotEmpty(ContentRegistry.AlloyRecipes);
        var parts = new List<byte>();
        foreach (var alloy in ContentRegistry.AlloyRecipes)
        {
            Assert.Equal(alloy.Parts.Sum(p => p.Ml), alloy.SetMl);
            Assert.DoesNotContain(alloy.ResultId, alloy.Parts.Select(p => p.MaterialId));
            parts.AddRange(alloy.Parts.Select(p => p.MaterialId));
        }
        Assert.Equal(parts.Count, parts.Distinct().Count());
    }

    [Fact]
    public void AWellFormedRecipeCompiles()
    {
        var (free1, free2, _) = Names();
        var result = ContentRegistry.MoltenMaterials.First(m => m.Name != free1 && m.Name != free2);
        var alloy = RecipeLoader.CompileAlloyRecipe(Definition(result.Name, (free1, 30), (free2, 70)), Path, "test");
        Assert.Equal(result.Id, alloy.ResultId);
        Assert.Equal(100, alloy.SetMl);
    }

    [Fact]
    public void ARecipeACrucibleCouldNotResolveIsRefused()
    {
        var (free1, free2, part) = Names();
        var result = ContentRegistry.MoltenMaterials.First(m => m.Name != free1 && m.Name != free2 && m.Name != part).Name;
        AlloyRecipeDefinition[] bad =
        [
            Definition(result, (free1, 100)),                       // a lone part
            Definition(free1, (free1, 100), (free2, 100)),          // the result is a part
            Definition(result, (free1, 100), (free1, 100)),         // a repeated part
            Definition(result, (free1, 100), (part, 100)),          // a part of another alloy
            Definition(result, (free1, 100), ("no_such_metal", 100)),
            Definition("no_such_metal", (free1, 100), (free2, 100)),
            Definition(result, (free1, 0), (free2, 100)),
            Definition(result, (free1, null), (free2, 100)),
        ];
        foreach (var definition in bad)
        {
            var error = Assert.Throws<InvalidOperationException>(() => RecipeLoader.CompileAlloyRecipe(definition, Path, "test"));
            Assert.Contains(Path, error.Message);
        }
    }
}
