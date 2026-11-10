using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;

namespace VoxelEngine.Simulation;

/// <summary>
/// The missiles a mob manager's kinds launch at players (a hell caster's fire bolts, a skeleton's arrows, the
/// Skeleton King's bones and slam shards): plain <see cref="ThrownProjectile"/>s flying under gravity, each carrying
/// its launcher's damage. Each update flies them, stops one on any target it touches (an immune target is spared),
/// records the first non-immune target it touched as a hit (<see cref="Hits"/>), and drops a missile that touched
/// someone or struck a block or outlived its flight. A missile never touches a mob, so a launcher's own kind is
/// never hurt by it. The owning manager bills the hits and adds its own extras (a burn, a trail).
/// GL-free, main thread only, allocation-free per frame.
/// </summary>
internal sealed class MobMissiles
{
    private readonly List<ThrownProjectile> _flying = [];
    // Parallel to _flying: the damage each missile carries (its launcher's, already scaled)
    private readonly List<int> _damage = [];
    private readonly List<MissileHit> _hits = [];

    /// <summary>The missiles in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Flying => _flying;

    /// <summary>The non-immune targets the missiles touched during the most recent <see cref="Update"/>.</summary>
    public IReadOnlyList<MissileHit> Hits => _hits;

    /// <summary>Launches a missile of item <paramref name="itemId"/> carrying <paramref name="damage"/>, falling under
    /// <paramref name="gravityScale"/> of the throw gravity and spent after <paramref name="lifeSeconds"/> (a bone
    /// shard's are shorter than an arrow's).</summary>
    public ThrownProjectile Launch(ushort itemId, Vector3 origin, Vector3 velocity, int damage, float gravityScale = 1f,
        float lifeSeconds = PlayerConfig.ThrowLifetimeSeconds)
    {
        var missile = new ThrownProjectile(itemId, origin, velocity, 0f, 1f, gravityScale, lifeSeconds);
        _flying.Add(missile);
        _damage.Add(damage);
        return missile;
    }

    /// <summary>
    /// Flies every missile one frame against <paramref name="targets"/>, refilling <see cref="Hits"/> with the
    /// first non-immune target each touched; a missile that touched any target, struck a block or outlived its
    /// flight is dropped.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        _hits.Clear();
        for (var i = _flying.Count - 1; i >= 0; i--)
        {
            var missile = _flying[i];
            var alive = missile.Update(deltaTime, chunks, null, out _);

            var touched = false;
            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                if (!MobTargets.BodyTouches(target.Eye, missile.Position, ThrownProjectile.HalfExtent)) continue;
                touched = true;
                if (target.IsImmune) continue;
                _hits.Add(new MissileHit(target, _damage[i], missile.Position));
                break;
            }

            if (!touched && alive) continue;
            _flying.RemoveAt(i);
            _damage.RemoveAt(i);
        }
    }

    /// <summary>One missile hit: who it touched, the damage it carried and where it was.</summary>
    public readonly record struct MissileHit(IMobTarget Target, int Damage, Vector3 Position);
}
