using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A collectable block left behind by mining. A drop hovers where the block was, spinning and
/// breathing in scale, until the player comes close enough — then it accelerates towards them
/// and is collected on contact. Drops ignore terrain; they only ever move towards the player.
/// </summary>
public class ItemDrop
{
    /// <summary>Edge length of the floating cube, in blocks.</summary>
    public const float Size = 0.35f;

    private const float SpinRadiansPerSecond = 1.5f;

    // Gentle breathing, as a fraction of Size, over PulsePeriod seconds
    private const float PulseAmount = 0.08f;
    private const float PulsePeriod = 1.4f;

    // Speeds in blocks/s. The drop eases from a drift into a fast whip, so
    // being pulled in reads as deliberate rather than instant.
    private const float DriftSpeed = 1f;
    private const float SuckSpeed = 9f;
    private const float RampSeconds = 0.35f;

    // Close enough to count as collected, so a drop never hovers just short of the target
    private const float CollectDistance = 0.2f;

    // A drop chasing a player faster than SuckSpeed is collected anyway rather than trailing
    // them forever
    private const float MaxAttractSeconds = 3f;

    // Grace period before a fresh drop can be pulled in. Without it a block mined at arm's
    // length is collected the same instant it appears and its spin is never seen.
    private const float PickupDelaySeconds = 0.4f;

    public byte BlockId { get; }
    public Vector3 Position { get; private set; }

    private float _age;
    private float _attractElapsed;
    private bool _attracted;

    /// <summary>Whether the drop has committed to flying at the player.</summary>
    public bool IsAttracted => _attracted;

    /// <summary>Continuous spin about the Y axis, in radians.</summary>
    public float Spin => _age * SpinRadiansPerSecond;

    /// <summary>Current edge length, including the idle pulse.</summary>
    public float Scale => Size * (1f + PulseAmount * MathUtils.Oscillate(_age, PulsePeriod));

    public ItemDrop(byte blockId, Vector3 position)
    {
        BlockId = blockId;
        Position = position;
    }

    /// <summary>
    /// Advances the drop, returning true once it reaches <paramref name="target"/> and should be
    /// collected. <paramref name="inRange"/> latches attraction on, so a drop already being
    /// pulled keeps coming even if the player backs away.
    /// </summary>
    public bool Update(float deltaTime, Vector3 target, bool inRange)
    {
        _age += deltaTime;

        if (!_attracted)
        {
            if (!inRange || _age < PickupDelaySeconds) return false;
            _attracted = true;
        }

        _attractElapsed += deltaTime;
        if (_attractElapsed >= MaxAttractSeconds) return true;

        var toTarget = target - Position;
        var distance = toTarget.Length();
        if (distance <= CollectDistance) return true;

        var speed = MathUtils.Lerp(DriftSpeed, SuckSpeed,
            MathUtils.EaseInCubic(_attractElapsed / RampSeconds));
        var step = speed * deltaTime;

        // Collect rather than overshoot, so a fast drop never orbits the player
        if (step >= distance) return true;

        Position += toTarget / distance * step;
        return false;
    }
}
