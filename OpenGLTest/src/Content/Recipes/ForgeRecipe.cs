namespace VoxelEngine.Content.Recipes;

/// <summary>One compiled anvil recipe: so many of one solid metal stacked on an anvil forge into the result
/// (<see cref="Simulation.Smithing.AnvilManager.TryForge"/>). One recipe per result.</summary>
public sealed class ForgeRecipe
{
    public ForgeRecipe(string name, byte metalId, int metalCount, byte resultId, int resultCount)
    {
        Name = name;
        MetalId = metalId;
        MetalCount = metalCount;
        ResultId = resultId;
        ResultCount = resultCount;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>The metal the anvil must hold.</summary>
    public byte MetalId { get; }

    /// <summary>How many of the metal the forge takes off the anvil.</summary>
    public int MetalCount { get; }

    /// <summary>What the forge makes.</summary>
    public byte ResultId { get; }

    /// <summary>How many of the result one forge makes.</summary>
    public int ResultCount { get; }
}
