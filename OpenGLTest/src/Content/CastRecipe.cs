namespace silkgltriangle;

/// <summary>
/// One compiled metalworks recipe: pouring <see cref="AmountMl"/> of <see cref="MaterialId"/>
/// into <see cref="MoldId"/> and letting it cool for <see cref="CoolSeconds"/> yields
/// <see cref="ResultCount"/> of <see cref="ResultId"/>. Keyed by (mold, material), so the same
/// mold casts a different metal into a different result.
/// </summary>
public sealed class CastRecipe
{
    public CastRecipe(
        string name,
        byte moldId,
        byte materialId,
        int amountMl,
        float coolSeconds,
        byte resultId,
        int resultCount)
    {
        Name = name;
        MoldId = moldId;
        MaterialId = materialId;
        AmountMl = amountMl;
        CoolSeconds = coolSeconds;
        ResultId = resultId;
        ResultCount = resultCount;
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>Fired mold block the metal is poured into.</summary>
    public byte MoldId { get; }

    /// <summary>Molten material poured in (<see cref="MoltenMaterial.Id"/>).</summary>
    public byte MaterialId { get; }

    /// <summary>Millilitres the mold takes before it is full.</summary>
    public int AmountMl { get; }

    /// <summary>Seconds a full mold cools before the cast can be collected.</summary>
    public float CoolSeconds { get; }

    /// <summary>Block or item the cooled cast yields.</summary>
    public byte ResultId { get; }

    /// <summary>How many of the result one cast makes.</summary>
    public int ResultCount { get; }
}
