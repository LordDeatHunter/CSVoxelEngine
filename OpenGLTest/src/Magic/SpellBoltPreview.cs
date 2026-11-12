using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Spells;
using VoxelEngine.Entities;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Magic;

/// <summary>
/// A casting client's own bolts, drawn only as sparks, GL-free, main thread only. The server flies the
/// real bolt and sends none of it, so without these the shooter would see nothing leave the staff. Each
/// flies the same straight line as the server's bolt (it reuses <see cref="SpellProjectile"/>'s flight and
/// stop test, so it stops at the first solid or non-air cell and at a mob mirror) and dies where that one
/// would land. It never strikes a mob, applies an effect or writes a cell. Remote players' bolts are not shown.
/// </summary>
internal sealed class SpellBoltPreview
{
    private readonly List<SpellProjectile> _bolts = [];

    /// <summary>The preview bolts in flight, oldest first.</summary>
    internal IReadOnlyList<SpellProjectile> Bolts => _bolts;

    /// <summary>
    /// Launches a preview of a <paramref name="caster"/>'s bolt from <paramref name="eye"/> along
    /// <paramref name="direction"/>, sparking in <paramref name="colorSrgb"/>; past
    /// <see cref="SpellConfig.MaxLiveProjectiles"/> the oldest is dropped.
    /// </summary>
    internal void Launch(PlayerContext caster, SpellPart element, SpellStats stats, Vector3 eye, Vector3 direction,
        Vector3 colorSrgb)
    {
        if (_bolts.Count >= SpellConfig.MaxLiveProjectiles)
            _bolts.RemoveAt(0);
        _bolts.Add(new SpellProjectile(caster, element, stats, eye, direction) { SparkColor = colorSrgb });
    }

    /// <summary>
    /// Flies every preview bolt: a flying one leaves its trail, a landed one bursts into impact sparks and
    /// its position is added to <paramref name="landings"/> (the caller clears it), a spent one just goes.
    /// </summary>
    internal void Update(float deltaTime, ChunkManager chunks, IReadOnlyList<Mob>? mobs, BlockDebrisSystem debris,
        List<Vector3> landings)
    {
        for (var i = _bolts.Count - 1; i >= 0; i--)
        {
            var bolt = _bolts[i];
            var flight = bolt.Update(deltaTime, chunks, mobs);
            if (flight == SpellFlight.Flying)
            {
                SpellSparks.Trail(debris, bolt, deltaTime);
                continue;
            }

            _bolts.RemoveAt(i);
            if (flight == SpellFlight.Spent) continue;
            SpellSparks.Impact(debris, bolt.Position, bolt.SparkColor);
            landings.Add(bolt.Position);
        }
    }
}
