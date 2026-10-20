namespace VoxelEngine.Player.Posing;

/// <summary>The body-wide offsets the second pass adds on top of the pose (see <see cref="SecondaryMotion.ComputeMods"/>).</summary>
internal readonly struct PoseMods
{
    public float HipBend { get; init; }
    public float KneeBend { get; init; }
    public float Shoulders { get; init; }
    public float RightShoulderExtra { get; init; }
    public float TorsoPitch { get; init; }
    public float Drop { get; init; }
    public float TorsoYaw { get; init; }
    public float TorsoRoll { get; init; }
    public float HeadPitch { get; init; }
    public float HeadYaw { get; init; }
    public float SwimPitch { get; init; }
    public float RootYaw { get; init; }
    public float SwimRoll { get; init; }
    public float DeathPitch { get; init; }
}
