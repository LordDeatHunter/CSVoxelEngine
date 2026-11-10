namespace VoxelEngine.Content.Recipes;

/// <summary>One compiled quern recipe: crushable input and the crushed result.</summary>
public sealed class CrushRecipe
{
    public CrushRecipe(string name, ushort inputId, ushort resultId, int resultCount)
    {
        Name = name;
        InputId = inputId;
        ResultId = resultId;
        ResultCount = resultCount;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>Crushable id that goes into the quern, one unit at a time.</summary>
    public ushort InputId { get; }

    /// <summary>Crushed id a successful rotation throws out.</summary>
    public ushort ResultId { get; }

    /// <summary>How many of the result one crush makes.</summary>
    public int ResultCount { get; }
}
