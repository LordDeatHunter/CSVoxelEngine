using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Entities;

/// <summary>
/// GL-free vantage seeking for a ranged mob whose line to its target is blocked (the skeleton archer): instead of
/// pushing straight into the obstruction it walks around the target to a spot it can shoot from. At most every
/// <see cref="MobConfig.RealmMobLosRetrySeconds"/> it samples spots on arcs around the target (its own distance kept
/// <see cref="MobConfig.VantageBandMarginBlocks"/> inside the range band, and one <see cref="MobConfig.VantageInwardBlocks"/> nearer), each
/// <see cref="MobConfig.VantageArcStepBlocks"/> of arc further to either side, nearest first and the side it already
/// walks first; a spot counts when the body can stand there (solid ground no deeper than
/// <see cref="MobConfig.MobLedgeMaxDropBlocks"/>, at most a step up, its height clear) and walk to it in a straight
/// line, and it wins when the line from it to the target's chest is clear. With no such spot it strafes on toward the
/// farthest standable one (the same side first), and a body that made less than
/// <see cref="MobConfig.VantageMinProgressBlocks"/> since the last pick turns to the other side. One instance per mob,
/// main thread only (it reads the live chunk map).
/// </summary>
public sealed class VantageSeeker
{
    private Vector3? _goal;
    private Vector3 _lastPick;
    private bool _walkedLastPick;
    private float _retry;
    private int _side = 1;

    /// <summary>Counts the re-pick throttle down by <paramref name="deltaTime"/>.</summary>
    public void Tick(float deltaTime) => _retry = MathF.Max(0f, _retry - deltaTime);

    /// <summary>Forgets the goal, so the next <see cref="Move"/> picks at once: call it whenever the line is clear
    /// or the mob does something else.</summary>
    public void Clear()
    {
        _goal = null;
        _walkedLastPick = false;
        _retry = 0f;
    }

    /// <summary>
    /// The frame's horizontal move (a unit vector, or zero once at the spot) for a body centered at
    /// <paramref name="center"/> and <paramref name="bodyHeight"/> tall, whose line leaves from
    /// <paramref name="lineFrom"/>, toward a spot from which that line (at the same height over the feet) reaches
    /// <paramref name="targetChest"/>, keeping between <paramref name="minRange"/> and <paramref name="maxRange"/>
    /// blocks of the target. Zero as well while the line from <paramref name="lineFrom"/> is already clear. Null when
    /// no standable spot is in reach at all, so the caller falls back to its own move.
    /// </summary>
    public Vector3? Move(Vector3 center, Vector3 lineFrom, float bodyHeight, Vector3 targetChest, float minRange,
        float maxRange, ChunkManager chunks)
    {
        if (_retry <= 0f)
        {
            _retry = MobConfig.RealmMobLosRetrySeconds;
            Repick(center, lineFrom, bodyHeight, targetChest, minRange, maxRange, chunks);
        }
        if (_goal is not { } goal) return null;

        var to = new Vector3(goal.X - center.X, 0f, goal.Z - center.Z);
        var dist = to.Length();
        return dist <= MobConfig.VantageArriveBlocks ? Vector3.Zero : to / dist;
    }

    private void Repick(Vector3 center, Vector3 lineFrom, float bodyHeight, Vector3 targetChest, float minRange,
        float maxRange, ChunkManager chunks)
    {
        // Walked toward a spot since the last pick but barely moved (a wall, a ledge): try the other side first
        var moved = new Vector2(center.X - _lastPick.X, center.Z - _lastPick.Z).Length();
        if (_walkedLastPick && moved < MobConfig.VantageMinProgressBlocks) _side = -_side;

        _lastPick = center;
        _goal = Pick(center, lineFrom, center.Y - bodyHeight * 0.5f, bodyHeight, targetChest, minRange, maxRange,
            chunks);
        _walkedLastPick = _goal is { } goal
            && new Vector2(goal.X - center.X, goal.Z - center.Z).Length() > MobConfig.VantageArriveBlocks;
    }

    // The nearest standable, reachable spot with a clear line (here, when the line from here is already clear), else
    // the farthest standable one on the preferred side (then the other), else null
    private Vector3? Pick(Vector3 center, Vector3 lineFrom, float feetY, float bodyHeight, Vector3 chest,
        float minRange, float maxRange, ChunkManager chunks)
    {
        if (!BlockRaycaster.SegmentHitsBlock(lineFrom, chest, chunks)) return center;

        var lineLift = lineFrom.Y - feetY;
        var from = new Vector3(center.X, lineFrom.Y, center.Z);

        var away = new Vector3(center.X - chest.X, 0f, center.Z - chest.Z);
        var dist = away.Length();
        if (dist < 1e-3f) return null;
        away /= dist;
        // Kept a margin inside the band, so the walk there (a chord, inside the arc) never leaves it
        var near = minRange + MobConfig.VantageBandMarginBlocks;
        var far = MathF.Max(near, maxRange - MobConfig.VantageBandMarginBlocks);
        var outer = Math.Clamp(dist, near, far);
        var inner = MathF.Max(near, outer - MobConfig.VantageInwardBlocks);
        var radii = inner < outer ? 2 : 1;

        Vector3? fallback = null;
        var fallbackScore = -1;
        var fallbackSide = _side;
        for (var step = 1; step <= MobConfig.VantageArcSteps; step++)
        for (var pass = 0; pass < 2; pass++)
        {
            var side = pass == 0 ? _side : -_side;
            for (var ring = 0; ring < radii; ring++)
            {
                var radius = ring == 0 ? outer : inner;
                var angle = side * step * MobConfig.VantageArcStepBlocks / radius;
                var (sin, cos) = MathF.SinCos(angle);
                var x = chest.X + (away.X * cos - away.Z * sin) * radius;
                var z = chest.Z + (away.X * sin + away.Z * cos) * radius;
                if (!TryStandAt(chunks, x, z, feetY, bodyHeight, out var standFeet)) continue;

                // The walk there must be straight and open at the line's height (over a one-block step)
                if (BlockRaycaster.SegmentHitsBlock(from, new Vector3(x, standFeet + lineLift, z), chunks)) continue;

                var spot = new Vector3(x, standFeet, z);
                if (!BlockRaycaster.SegmentHitsBlock(spot with { Y = standFeet + lineLift }, chest, chunks))
                {
                    _side = side;
                    return spot;
                }

                var score = step + (pass == 0 ? MobConfig.VantageArcSteps : 0);
                if (score <= fallbackScore) continue;
                fallbackScore = score;
                fallback = spot;
                fallbackSide = side;
            }
        }

        _side = fallbackSide;
        return fallback;
    }

    // The feet height of a body standing in column (x, z) near feetY: solid ground under it at most a step up or a
    // safe drop down, and its whole height of loaded cells clear of solids and fluids
    private static bool TryStandAt(ChunkManager chunks, float x, float z, float feetY, float bodyHeight,
        out float standFeet)
    {
        var cx = MathUtils.FloorToInt(x);
        var cz = MathUtils.FloorToInt(z);
        var level = MathUtils.FloorToInt(feetY + 0.05f);
        var clearCells = (int)MathF.Ceiling(bodyHeight);
        for (var y = level + 1; y >= level - (int)MobConfig.MobLedgeMaxDropBlocks; y--)
        {
            if (!IsStandable(chunks, cx, y, cz, clearCells)) continue;
            standFeet = y;
            return true;
        }
        standFeet = 0f;
        return false;
    }

    private static bool IsStandable(ChunkManager chunks, int x, int feet, int z, int clearCells)
    {
        if (!chunks.TryGetCell(x, feet - 1, z, out var ground, out _) || !BlockType.IsSolid(ground)) return false;
        for (var k = 0; k < clearCells; k++)
        {
            if (!chunks.TryGetCell(x, feet + k, z, out var id, out _)) return false;
            if (BlockType.IsSolid(id) || BlockType.IsFluid(id)) return false;
        }
        return true;
    }
}
