namespace silkgltriangle;

/// <summary>One compiled quern recipe: crushable input and the crushed result.</summary>
public sealed class CrushRecipe
{
    public CrushRecipe(string name, byte inputId, byte resultId, int resultCount)
    {
        Name = name;
        InputId = inputId;
        ResultId = resultId;
        ResultCount = resultCount;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>Crushable id that goes into the quern, one unit at a time.</summary>
    public byte InputId { get; }

    /// <summary>Crushed id a successful rotation throws out.</summary>
    public byte ResultId { get; }

    /// <summary>How many of the result one crush makes.</summary>
    public int ResultCount { get; }
}
