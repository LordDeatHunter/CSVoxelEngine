namespace silkgltriangle;

/// <summary>
/// Sun and block light (each 0..1) one entity draw is lit with, plus the
/// <see cref="BlockLightPalette"/> slot of that block light (0, the default warm colour, unless a
/// coloured lamp lights the cell), fed to the plain world vertex path as per-draw uniforms (see
/// <see cref="EntityLight"/>).
/// </summary>
public readonly record struct EntityLightSample(float Sun, float Block, byte Tint = 0)
{
    /// <summary>The <see cref="Vertex"/> defaults: full sun, no block light.</summary>
    public static readonly EntityLightSample Default = new(1f, 0f);

    /// <summary>What <c>Primitives.CreateFlatQuad</c> bakes for the self-lit molten pool.</summary>
    public static readonly EntityLightSample SelfLit = new(1f, 1f);
}
