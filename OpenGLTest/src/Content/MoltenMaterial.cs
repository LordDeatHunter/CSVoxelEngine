using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// One molten metal a crucible can hold. Molten material is content rather than a
/// <see cref="FluidManager"/> fluid: it never occupies a cell, it only rides on a crucible's
/// stack and on the cast a mold is filling, so it needs an id, a tile and a tint and nothing
/// else. Ids are handed out 1-based in file-name order, which leaves 0 free as "no material".
/// </summary>
public sealed class MoltenMaterial
{
    public MoltenMaterial(byte id, string name, string displayName, string textureName, Rgba color)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        TextureName = textureName;
        Color = color;
    }

    /// <summary>1-based id, in file-name order; 0 means "no material".</summary>
    public byte Id { get; }

    /// <summary>File name, which is how melt and cast recipes name this material.</summary>
    public string Name { get; }

    /// <summary>Display name for tooltips and previews.</summary>
    public string DisplayName { get; }

    /// <summary>Atlas tile name the molten pool is drawn with.</summary>
    public string TextureName { get; }

    /// <summary>Tint multiplied into that tile, so one lava tile serves every metal.</summary>
    public Rgba Color { get; }

    /// <summary>
    /// Atlas slice of <see cref="TextureName"/>, resolved in
    /// <see cref="ContentRegistry.BakeModels"/> once the atlas exists.
    /// </summary>
    public ushort Tile { get; internal set; }
}
