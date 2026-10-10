namespace silkgltriangle;

/// <summary>What a <see cref="BlockDebris"/> chip looks like, i.e. which cached cube
/// <see cref="BlockCubeCache"/> draws it with.</summary>
public enum DebrisLook : byte
{
    /// <summary>A chip broken out of the block: its whole cube, each face as the block wears it.</summary>
    Block,

    /// <summary>A chip scuffed off the block's surface (landing or footstep dust): its top face on
    /// every side, so dust off a grass block is green grass, not mostly dirt sides.</summary>
    Surface,

    /// <summary>A chip of one flat colour that is no block at all (a slime's goo): a grey cube tinted
    /// by the chip's <see cref="BlockDebris.Tint"/>; its block id is air.</summary>
    Colour,
}
