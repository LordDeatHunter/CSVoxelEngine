using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.Status;

namespace VoxelEngine.Tests.Support;

/// <summary>A test <see cref="IMobTarget"/>: a body at a settable eye that may fly or be dead, counting the
/// damage it takes.</summary>
internal sealed class StubTarget(Vector3 eye) : IMobTarget
{
    public Vector3 Eye { get; set; } = eye;

    /// <summary>The velocity the target reports; at rest unless a test sets it.</summary>
    public Vector3 Velocity { get; set; }
    public bool IsImmune => IsFlying || IsDead;
    public bool IsFlying { get; set; }
    public bool IsDead { get; set; }

    /// <summary>The target's status effects; none (null) unless a test gives it a set.</summary>
    public StatusEffectSet? Effects { get; init; }

    /// <summary>The damage every hit that landed on this target dealt, summed.</summary>
    public int DamageTaken { get; private set; }

    public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null) =>
        DamageTaken += damage;

    /// <summary>The eye of a body standing with its feet at <paramref name="feet"/>.</summary>
    public static Vector3 EyeAt(Vector3 feet) => feet + new Vector3(0f, Camera.EyeHeight, 0f);
}
