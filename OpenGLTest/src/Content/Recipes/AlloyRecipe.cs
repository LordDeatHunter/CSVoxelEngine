using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Stations;

namespace VoxelEngine.Content.Recipes;

/// <summary>
/// One compiled alloy recipe: a full set of <see cref="Parts"/> (each a molten material and its
/// ml) charged into a crucible in an alloy kiln becomes <see cref="SetMl"/> of
/// <see cref="ResultId"/>, exactly the sum of the parts, so alloying never creates or destroys
/// metal (see <see cref="Simulation.AlloyRules"/>).
/// </summary>
public sealed class AlloyRecipe
{
    public AlloyRecipe(string name, byte resultId, IReadOnlyList<(byte MaterialId, int Ml)> parts)
    {
        Name = name;
        ResultId = resultId;
        Parts = parts;
        SetMl = parts.Sum(p => p.Ml);
    }

    /// <summary>Recipe file name, for error messages.</summary>
    public string Name { get; }

    /// <summary>Molten material a set alloys into (<see cref="MoltenMaterial.Id"/>).</summary>
    public byte ResultId { get; }

    /// <summary>Each part's molten material and the ml one set takes of it; distinct materials,
    /// none of them the result.</summary>
    public IReadOnlyList<(byte MaterialId, int Ml)> Parts { get; }

    /// <summary>Milliliters one set takes, and yields.</summary>
    public int SetMl { get; }

    /// <summary>Whether <paramref name="materialId"/> is one of the parts.</summary>
    public bool HasPart(byte materialId) => Parts.Any(p => p.MaterialId == materialId);

    /// <summary>Whether <paramref name="materialId"/> is a part or the result: what may share a
    /// crucible while this alloy is made.</summary>
    public bool Involves(byte materialId) => materialId == ResultId || HasPart(materialId);
}
