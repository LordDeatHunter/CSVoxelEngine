namespace VoxelEngine.Content.Recipes;

/// <summary>One compiled campfire recipe: raw input, cooked result and seconds on the fire.</summary>
public sealed class CookRecipe
{
    public CookRecipe(string name, byte inputId, byte resultId, int resultCount, float cookSeconds)
    {
        Name = name;
        InputId = inputId;
        ResultId = resultId;
        ResultCount = resultCount;
        CookSeconds = cookSeconds;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>Raw id that goes onto the campfire.</summary>
    public byte InputId { get; }

    /// <summary>Cooked id it converts into.</summary>
    public byte ResultId { get; }

    /// <summary>How many of the result one cook makes.</summary>
    public int ResultCount { get; }

    /// <summary>Seconds on a lit campfire before the input converts.</summary>
    public float CookSeconds { get; }
}
