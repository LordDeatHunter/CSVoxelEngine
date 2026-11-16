namespace VoxelEngine.Content.Recipes;

/// <summary>One compiled anvil recipe: so many of one solid metal stacked on an anvil, picked from the anvil's list and
/// hammered <see cref="Hits"/> times, forge into the result and its byproduct
/// (<see cref="Simulation.Smithing.AnvilManager.TryHit"/>). One recipe per result.</summary>
public sealed class ForgeRecipe
{
    public ForgeRecipe(string name, ushort metalId, int metalCount, ushort resultId, int resultCount, int hits,
        ushort byproductId = 0, int byproductCount = 0)
    {
        Hits = hits;
        ByproductId = byproductId;
        ByproductCount = byproductCount;
        Name = name;
        MetalId = metalId;
        MetalCount = metalCount;
        ResultId = resultId;
        ResultCount = resultCount;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>The metal the anvil must hold.</summary>
    public ushort MetalId { get; }

    /// <summary>How many of the metal the forge takes off the anvil.</summary>
    public int MetalCount { get; }

    /// <summary>What the forge makes.</summary>
    public ushort ResultId { get; }

    /// <summary>How many of the result one forge makes.</summary>
    public int ResultCount { get; }

    /// <summary>Hammer hits the forge takes once the recipe is picked.</summary>
    public int Hits { get; }

    /// <summary>What the hammering squeezes out besides the result (a bloom's slag), 0 for nothing.</summary>
    public ushort ByproductId { get; }

    /// <summary>How many of <see cref="ByproductId"/> one forge leaves.</summary>
    public int ByproductCount { get; }
}
