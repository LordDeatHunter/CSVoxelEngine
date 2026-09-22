namespace silkgltriangle;

/// <summary>
/// One compiled melt recipe: what a crucible turns one unit of an input into. There is at most
/// one per input, so <see cref="ContentRegistry.MeltRecipeForInput"/> is both the lookup and the
/// "is this meltable" test.
/// </summary>
public sealed class MeltRecipe
{
    public MeltRecipe(string name, byte inputId, byte materialId, int amountMl)
    {
        Name = name;
        InputId = inputId;
        MaterialId = materialId;
        AmountMl = amountMl;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>Block or item id that goes into the crucible, one unit at a time.</summary>
    public byte InputId { get; }

    /// <summary>Molten material it melts into (<see cref="MoltenMaterial.Id"/>).</summary>
    public byte MaterialId { get; }

    /// <summary>Millilitres one unit yields.</summary>
    public int AmountMl { get; }
}
