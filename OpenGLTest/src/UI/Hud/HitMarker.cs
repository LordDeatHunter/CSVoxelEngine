namespace VoxelEngine.UI.Hud;

/// <summary>
/// The crosshair's hit marker, GL-free: an X flashes when a hit lands and fades out over
/// <see cref="Seconds"/>. The world shows it at the same connecting-hit sites the hit-stop uses,
/// so the flash and the freeze never disagree about what connected.
/// </summary>
public sealed class HitMarker
{
    /// <summary>Seconds the flash takes to fade out.</summary>
    public const float Seconds = 0.25f;

    private float _elapsed = Seconds;

    /// <summary>The flash's alpha: 1 at the hit, easing to 0 over <see cref="Seconds"/>.</summary>
    public float Alpha => 1f - MathUtils.SmoothStep(MathUtils.InverseLerp(0f, Seconds, _elapsed));

    /// <summary>Flashes the mark (a hit just landed).</summary>
    public void Show() => _elapsed = 0f;

    /// <summary>Ages the flash.</summary>
    public void Update(float deltaTime) => _elapsed = System.MathF.Min(Seconds, _elapsed + System.MathF.Max(0f, deltaTime));
}
