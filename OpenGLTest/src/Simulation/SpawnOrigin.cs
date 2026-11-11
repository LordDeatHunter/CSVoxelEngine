using System.Numerics;
using VoxelEngine.Entities;

namespace VoxelEngine.Simulation;

/// <summary>
/// Where a ring spawn search is centered and which way that center is moving: a target's eye and its
/// velocity (<see cref="Of"/>), so <see cref="MobSpawner.RingColumn"/> can put most spawns ahead of a running
/// player. A plain position converts to an origin at rest (a pack mate ringed on its leader, a camp ringed
/// on its fire, a test's fixed eye).
/// </summary>
public readonly record struct SpawnOrigin(Vector3 Eye, Vector3 Velocity)
{
    /// <summary>The origin of a search around <paramref name="target"/>: its eye, moving as it moves.</summary>
    public static SpawnOrigin Of(IMobTarget target) => new(target.Eye, target.Velocity);

    public static implicit operator SpawnOrigin(Vector3 eye) => new(eye, Vector3.Zero);
}
