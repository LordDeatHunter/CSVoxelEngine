using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Stations;
using VoxelEngine.Data;

namespace VoxelEngine.Content.Recipes;

/// <summary>Loads <c>data/recipes</c> (crafting), <c>recipes/cook</c>, <c>recipes/crush</c>, <c>data/molten</c>, <c>recipes/melt</c>, <c>recipes/cast</c>, <c>recipes/alloy</c> and <c>recipes/forge</c> into the compiled lists <see cref="ContentRegistry"/> serves; a missing folder is no recipes.</summary>
internal static class RecipeLoader
{
    internal static void LoadRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.Recipes.Add(CompileRecipe(name, ContentFiles.ReadJson<RecipeDefinition>(path), path));
    }

    internal static void LoadCookRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.CookRecipes.Add(CompileCookRecipe(name, ContentFiles.ReadJson<CookRecipeDefinition>(path), path));
    }

    internal static void LoadCrushRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.CrushRecipes.Add(CompileCrushRecipe(name, ContentFiles.ReadJson<CrushRecipeDefinition>(path), path));
    }

    internal static void LoadMoltenMaterials(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.MoltenMaterialsList.Add(CompileMoltenMaterial(name, ContentFiles.ReadJson<MoltenMaterialDefinition>(path), path));
    }

    internal static void LoadMeltRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.MeltRecipes.Add(CompileMeltRecipe(name, ContentFiles.ReadJson<MeltRecipeDefinition>(path), path));
    }

    internal static void LoadCastRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.CastRecipes.Add(CompileCastRecipe(name, ContentFiles.ReadJson<CastRecipeDefinition>(path), path));
    }

    internal static void LoadAlloyRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.AlloyRecipesList.Add(CompileAlloyRecipe(ContentFiles.ReadJson<AlloyRecipeDefinition>(path), path, name));
    }

    internal static void LoadForgeRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.ForgeRecipesList.Add(CompileForgeRecipe(name, ContentFiles.ReadJson<ForgeRecipeDefinition>(path), path));
    }

    /// <summary>
    /// Compiles one anvil recipe: a registered metal taken 1..<see cref="MaxForgeMetalCount"/> at a time, a registered
    /// result other than the metal, made 1 or more at a time, and one recipe per result (a pick on the anvil names its
    /// recipe by the result). Every error names <paramref name="path"/>.
    /// </summary>
    private static ForgeRecipe CompileForgeRecipe(string name, ForgeRecipeDefinition definition, string path)
    {
        if (definition.Metal == null || !ContentFiles.TryResolve(definition.Metal, out var metalId))
            throw new InvalidOperationException(
                $"Forge recipe '{path}' names metal '{definition.Metal}', which is not a registered block or item.");
        var metalCount = definition.MetalCount ?? 0;
        if (metalCount is < 1 or > MaxForgeMetalCount)
            throw new InvalidOperationException(
                $"Forge recipe '{path}' has metalCount {definition.MetalCount}; expected 1..{MaxForgeMetalCount}.");
        if (definition.Result == null || !ContentFiles.TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Forge recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        if (resultId == metalId)
            throw new InvalidOperationException($"Forge recipe '{path}' forges its metal into itself.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Forge recipe '{path}' has count {resultCount}; expected 1 or more.");
        if (ContentRegistry.ForgeRecipeForResult(resultId) is { } other)
            throw new InvalidOperationException(
                $"Forge recipe '{path}' duplicates the result of '{other.Name}': one forge recipe per result.");
        return new ForgeRecipe(name, metalId, metalCount, resultId, resultCount);
    }

    /// <summary>The most metal one forge recipe may take: an anvil's stack is capped by its metal's largest recipe,
    /// and the stack rides the wire in a byte.</summary>
    internal const int MaxForgeMetalCount = 64;

    /// <summary>
    /// Compiles one alloy recipe: a molten result and at least two parts, each a distinct molten
    /// material other than the result with 1..10000 ml, and no material may be a part of two
    /// alloys (a vessel holding it could not tell which set it is in). Pure apart from the molten
    /// material lookup and the alloys already compiled; every error names <paramref name="path"/>.
    /// </summary>
    internal static AlloyRecipe CompileAlloyRecipe(AlloyRecipeDefinition definition, string path, string name)
    {
        var result = definition.Result == null ? null : ContentRegistry.MoltenMaterialByName(definition.Result);
        if (result == null)
            throw new InvalidOperationException(
                $"Alloy recipe '{path}' names result '{definition.Result}', which is not a molten material; known materials: {KnownMoltenMaterials()}.");
        if (definition.Parts is not { Count: >= 2 } partDefinitions)
            throw new InvalidOperationException($"Alloy recipe '{path}' needs at least two parts.");
        var parts = new List<(byte MaterialId, int Ml)>();
        foreach (var part in partDefinitions)
        {
            var material = part.Fluid == null ? null : ContentRegistry.MoltenMaterialByName(part.Fluid);
            if (material == null)
                throw new InvalidOperationException(
                    $"Alloy recipe '{path}' names part '{part.Fluid}', which is not a molten material; known materials: {KnownMoltenMaterials()}.");
            if (material.Id == result.Id || parts.Any(p => p.MaterialId == material.Id))
                throw new InvalidOperationException(
                    $"Alloy recipe '{path}' names '{part.Fluid}' twice; parts are distinct and never the result.");
            var ml = part.Ml ?? 0;
            if (ml is < 1 or > 10000)
                throw new InvalidOperationException($"Alloy recipe '{path}' has part ml {part.Ml}; expected 1..10000.");
            if (ContentRegistry.AlloyRecipesList.FirstOrDefault(a => a.HasPart(material.Id)) is { } other)
                throw new InvalidOperationException(
                    $"Alloy recipe '{path}' shares the part '{part.Fluid}' with '{other.Name}': a metal is a part of one alloy at most.");
            parts.Add((material.Id, ml));
        }
        return new AlloyRecipe(name, result.Id, parts);
    }

    private static MoltenMaterial CompileMoltenMaterial(string name, MoltenMaterialDefinition definition, string path)
    {
        // Ids are 1-based positions in file-name order, so 0 stays free as "no material".
        if (ContentRegistry.MoltenMaterialsList.Count >= 255)
            throw new InvalidOperationException(
                $"Molten material '{path}' exceeds the 255 ids a molten material id can hold.");
        if (string.IsNullOrWhiteSpace(definition.Texture))
            throw new InvalidOperationException($"Molten material '{path}' names no texture; one atlas tile is required.");
        if (definition.Color is not { Length: 3 })
            throw new InvalidOperationException(
                $"Molten material '{path}' has {definition.Color?.Length ?? 0} color components; expected exactly 3 (r, g, b in 0..255).");
        if (definition.Color.Any(component => !float.IsFinite(component) || component < 0f || component > 255f))
        {
            throw new InvalidOperationException(
                $"Molten material '{path}' has an invalid color component; expected 0..255.");
        }

        var color = new Rgba((byte)definition.Color[0], (byte)definition.Color[1], (byte)definition.Color[2]);
        return new MoltenMaterial(
            (byte)(ContentRegistry.MoltenMaterialsList.Count + 1), name, definition.Name ?? name, definition.Texture!, color);
    }

    private static MeltRecipe CompileMeltRecipe(string name, MeltRecipeDefinition definition, string path)
    {
        if (definition.Input == null || !ContentFiles.TryResolve(definition.Input, out var inputId))
            throw new InvalidOperationException(
                $"Melt recipe '{path}' names input '{definition.Input}', which is not a registered block or item.");
        var material = definition.Fluid == null ? null : ContentRegistry.MoltenMaterialByName(definition.Fluid);
        if (material == null)
            throw new InvalidOperationException(
                $"Melt recipe '{path}' names fluid '{definition.Fluid}', which is not a molten material; known materials: {KnownMoltenMaterials()}.");
        var amountMl = definition.AmountMl ?? 0;
        if (amountMl < 1 || amountMl > 10000)
            throw new InvalidOperationException(
                $"Melt recipe '{path}' has amountMl {definition.AmountMl}; expected 1..10000.");
        if (ContentRegistry.MeltRecipes.Any(r => r.InputId == inputId))
            throw new InvalidOperationException(
                $"Melt recipe '{path}' duplicates the input of '{ContentRegistry.MeltRecipes.First(r => r.InputId == inputId).Name}': one melt recipe per input.");
        return new MeltRecipe(name, inputId, material.Id, amountMl);
    }

    private static CastRecipe CompileCastRecipe(string name, CastRecipeDefinition definition, string path)
    {
        if (definition.Mold == null || !ContentFiles.TryResolve(definition.Mold, out var moldId) || BlockType.ById[moldId] == null)
            throw new InvalidOperationException(
                $"Cast recipe '{path}' names mold '{definition.Mold}', which is not a registered block.");
        var material = definition.Fluid == null ? null : ContentRegistry.MoltenMaterialByName(definition.Fluid);
        if (material == null)
            throw new InvalidOperationException(
                $"Cast recipe '{path}' names fluid '{definition.Fluid}', which is not a molten material; known materials: {KnownMoltenMaterials()}.");
        var amountMl = definition.AmountMl ?? 0;
        if (amountMl < 1 || amountMl > 10000)
            throw new InvalidOperationException(
                $"Cast recipe '{path}' has amountMl {definition.AmountMl}; expected 1..10000.");
        var coolSeconds = definition.CoolSeconds ?? 0f;
        if (!float.IsFinite(coolSeconds) || coolSeconds <= 0f || coolSeconds > 600f)
            throw new InvalidOperationException(
                $"Cast recipe '{path}' has coolSeconds {definition.CoolSeconds}; expected 0..600 exclusive of 0.");
        if (definition.Result == null || !ContentFiles.TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Cast recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Cast recipe '{path}' has count {resultCount}; expected 1 or more.");
        if (ContentRegistry.CastRecipes.Any(r => r.MoldId == moldId && r.MaterialId == material.Id))
            throw new InvalidOperationException(
                $"Cast recipe '{path}' duplicates the mold and fluid of '{ContentRegistry.CastRecipes.First(r => r.MoldId == moldId && r.MaterialId == material.Id).Name}': one cast recipe per mold and molten material.");
        return new CastRecipe(name, moldId, material.Id, amountMl, coolSeconds, resultId, resultCount);
    }

    private static string KnownMoltenMaterials() =>
        ContentRegistry.MoltenMaterialsList.Count == 0 ? "(none)" : string.Join(", ", ContentRegistry.MoltenMaterialsList.Select(m => m.Name));

    private static CookRecipe CompileCookRecipe(string name, CookRecipeDefinition definition, string path)
    {
        if (definition.Input == null || !ContentFiles.TryResolve(definition.Input, out var inputId))
            throw new InvalidOperationException(
                $"Cook recipe '{path}' names input '{definition.Input}', which is not a registered block or item.");
        if (definition.Result == null || !ContentFiles.TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Cook recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Cook recipe '{path}' has count {resultCount}; expected 1 or more.");
        var cookSeconds = definition.CookSeconds ?? 0f;
        if (!float.IsFinite(cookSeconds) || cookSeconds <= 0f || cookSeconds > 600f)
            throw new InvalidOperationException(
                $"Cook recipe '{path}' has cookSeconds {definition.CookSeconds}; expected 0..600 exclusive of 0.");
        if (ContentRegistry.CookRecipes.Any(r => r.InputId == inputId))
            throw new InvalidOperationException(
                $"Cook recipe '{path}' duplicates the input of '{ContentRegistry.CookRecipes.First(r => r.InputId == inputId).Name}': one cook recipe per input.");
        return new CookRecipe(name, inputId, resultId, resultCount, cookSeconds);
    }

    private static CrushRecipe CompileCrushRecipe(string name, CrushRecipeDefinition definition, string path)
    {
        if (definition.Input == null || !ContentFiles.TryResolve(definition.Input, out var inputId))
            throw new InvalidOperationException(
                $"Crush recipe '{path}' names input '{definition.Input}', which is not a registered block or item.");
        if (definition.Result == null || !ContentFiles.TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Crush recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Crush recipe '{path}' has count {resultCount}; expected 1 or more.");
        if (ContentRegistry.CrushRecipes.Any(r => r.InputId == inputId))
            throw new InvalidOperationException(
                $"Crush recipe '{path}' duplicates the input of '{ContentRegistry.CrushRecipes.First(r => r.InputId == inputId).Name}': one crush recipe per input.");
        return new CrushRecipe(name, inputId, resultId, resultCount);
    }

    private static CraftingRecipe CompileRecipe(string name, RecipeDefinition definition, string path)
    {
        if (definition.Result == null || !ContentFiles.TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Recipe '{path}' has count {resultCount}; expected 1 or more.");

        var ingredients = new List<CraftingRecipe.Ingredient>();
        foreach (var ingredient in definition.Ingredients ?? [])
        {
            var count = ingredient.Count ?? 1;
            if (count <= 0)
                throw new InvalidOperationException($"Recipe '{path}' has an ingredient with count {count}; expected 1 or more.");
            if (ingredient.Tag != null && ingredient.Item != null)
                throw new InvalidOperationException(
                    $"Recipe '{path}' names both item '{ingredient.Item}' and tag '{ingredient.Tag}'; an ingredient takes one or the other.");
            if (ingredient.Tag != null)
            {
                if (!ContentRegistry.Tags.TryGetValue(ingredient.Tag, out var members) || members.Count == 0)
                    throw new InvalidOperationException(
                        $"Recipe '{path}' names tag '{ingredient.Tag}', which has no loaded members.");
                ingredients.Add(new CraftingRecipe.Ingredient(0, count, ingredient.Tag,
                    members.OrderBy(id => id).ToArray()));
                continue;
            }
            if (ingredient.Item == null || !ContentFiles.TryResolve(ingredient.Item, out var id))
                throw new InvalidOperationException(
                    $"Recipe '{path}' names ingredient '{ingredient.Item}', which is not a registered block or item.");
            ingredients.Add(new CraftingRecipe.Ingredient(id, count));
        }
        if (ingredients.Count == 0)
            throw new InvalidOperationException($"Recipe '{path}' has no ingredients.");

        return new CraftingRecipe(name, resultId, resultCount, ingredients.ToArray());
    }
}
