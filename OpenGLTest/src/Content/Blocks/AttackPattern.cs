using VoxelEngine.Interaction;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// How a left-click attack behaves, from the optional <c>attack</c> field of
/// <c>assets/data/items/&lt;name&gt;.json</c> (absent means <see cref="Punch"/>).
/// <see cref="Punch"/> is the bare-hand swing: a narrow forward cone that hits the nearest
/// enemy once per the item's pace. <see cref="Slice"/> is a short blade's two-phase sweep: a
/// forward hemisphere that hits the nearest two enemies, with the return swing at half power.
/// <see cref="Sword"/> is a long blade's three-swing chain: a diagonal cut and a rising back cut
/// over the hemisphere, then an overhead chop on the one enemy under the aim.
/// See <see cref="Combat"/>, <see cref="SliceCombo"/> and <see cref="SwordCombo"/>.
/// </summary>
public enum AttackPattern
{
    /// <summary>Forward cone, nearest single enemy (<see cref="Combat.InPunchCone"/>).</summary>
    Punch,

    /// <summary>Forward hemisphere, nearest two enemies, two-phase swing (<see cref="SliceCombo"/>).</summary>
    Slice,

    /// <summary>The cut, back cut and chop chain (<see cref="SwordCombo"/>, <see cref="SwordSwings"/>).</summary>
    Sword,
}
