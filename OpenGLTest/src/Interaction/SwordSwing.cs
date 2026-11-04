namespace VoxelEngine.Interaction;

/// <summary>
/// The three swings of a sword's chain (<see cref="Content.Blocks.AttackPattern.Sword"/>), in the
/// order the combo runs them (<see cref="SwordCombo"/>): a diagonal cut down from high on the
/// right to low on the left, a rising back cut from low on the left up to the right, and an
/// overhead chop straight down. Each has its own reach, power, pace and animation
/// (<see cref="SwordSwings"/>, <see cref="Player.PlayerModel.SwordDuration"/>).
/// </summary>
public enum SwordSwing : byte
{
    /// <summary>The opener: a wide diagonal cut, high right to low left, over the forward hemisphere.</summary>
    Cut = 0,

    /// <summary>The second swing: a quicker rising cut back from low left to high right, at reduced power.</summary>
    BackCut = 1,

    /// <summary>The finisher: a slow overhead chop straight down onto the one target under the aim, at the
    /// highest power and a heavier shove.</summary>
    Chop = 2,
}
