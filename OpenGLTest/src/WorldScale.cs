namespace silkgltriangle;

/// <summary>
/// Physical scale of the world. One block is 0.5 m, so quantities defined in
/// real-world units (player size, speeds, gravity, reach) are converted to
/// block units by multiplying with BlocksPerMeter.
/// </summary>
public static class WorldScale
{
    public const float BlocksPerMeter = 2f;
}
