using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Entities;

/// <summary>GL-free pick of the boss the HUD's boss bar shows: the nearest living boss to the viewer.</summary>
public static class BossSelection
{
    /// <summary>The bar state of the boss nearest <paramref name="from"/> among <paramref name="mobs"/>, or null
    /// when none is a living boss. A dead or dying boss never counts.</summary>
    public static BossBarState? Nearest(IReadOnlyList<Mob> mobs, Vector3 from)
    {
        Mob? best = null;
        var bestDistance = float.MaxValue;
        foreach (var mob in mobs)
        {
            if (!mob.IsBoss || mob.IsDead || mob.IsDying)
                continue;
            var distance = Vector3.DistanceSquared(mob.Position, from);
            if (distance >= bestDistance)
                continue;
            best = mob;
            bestDistance = distance;
        }
        return best == null
            ? null
            : new BossBarState(best.Type.DisplayName, System.Math.Clamp(best.Health / (float)best.MaxHealth, 0f, 1f));
    }
}
