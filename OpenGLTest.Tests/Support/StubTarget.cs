using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Player;

namespace VoxelEngine.Tests.Support;

/// <summary>A test <see cref="IMobTarget"/>: a body at a settable eye that may fly or be dead.</summary>
internal sealed class StubTarget(Vector3 eye) : IMobTarget
{
    public Vector3 Eye { get; set; } = eye;
    public bool IsImmune => IsFlying || IsDead;
    public bool IsFlying { get; set; }
    public bool IsDead { get; set; }

    public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null)
    {
    }

    /// <summary>The eye of a body standing with its feet at <paramref name="feet"/>.</summary>
    public static Vector3 EyeAt(Vector3 feet) => feet + new Vector3(0f, Camera.EyeHeight, 0f);
}
