using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Stations;
using VoxelEngine.Data;

namespace VoxelEngine.Content.Recipes;

/// <summary>Loads <c>data/recipes</c> (crafting), <c>recipes/cook</c>, <c>recipes/crush</c>, <c>data/molten</c>, <c>recipes/melt</c> and <c>recipes/cast</c> into the compiled lists <see cref="ContentRegistry"/> serves; a missing folder is no recipes.</summary>
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
