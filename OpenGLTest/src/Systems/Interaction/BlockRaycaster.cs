using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free, main-thread voxel raycasting: the DDA walk the camera aims with and the
/// line-of-sight test combat uses. Extracted from <see cref="Camera"/> so the ray logic is a
/// pure function of the inputs rather than a method on the player.
/// </summary>
public static class BlockRaycaster
{
    /// <summary>
    /// Whether any targetable block cell lies on the segment from <paramref name="from"/> to
    /// <paramref name="to"/>: the line-of-sight test the punch uses so a slime behind a wall
    /// cannot be hit. Uses the same box-aware walk as <see cref="Raycast"/> (so flowers and
    /// thin wood are aimed through), and fluids never block.
    /// </summary>
    public static bool SegmentHitsBlock(Vector3 from, Vector3 to, ChunkManager chunkManager)
    {
        var delta = to - from;
        var distance = delta.Length();
        if (distance < 1e-4f) return false;

        // A diagonal segment crosses up to 3 cells per block travelled; cap the walk on cells
        // and stop on travelled distance, exactly like the capped shoulder-camera walk.
        var steps = Math.Max(1, (int)MathF.Ceiling(distance * 3f) + 2);
        return Raycast(from, delta / distance, distance, steps, stopAtRange: true,
            chunkManager, out _, out _, out _, out _, includeFluids: false);
    }

    /// <summary>
    /// The raw box-aware DDA walk: steps <paramref name="origin"/>/<paramref name="direction"/>
    /// cell by cell and reports the first targetable cell. A fluid is targetable only when
    /// <paramref name="includeFluids"/> is set and the cell is a source, and a cell only counts
    /// when the ray enters one of its <see cref="BlockHitbox"/>es. <paramref name="stopAtRange"/>
    /// chooses a cell-count cap (<paramref name="cellCap"/>) for capped walks or a travelled
    /// distance cap for first-person walks; <paramref name="hitT"/> is the distance along the
    /// ray to the hit.
    /// </summary>
    public static bool Raycast(Vector3 origin, Vector3 direction, float range, float cellCap, bool stopAtRange,
        ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal, out int hitBoxIndex,
        out float hitT, bool includeFluids)
    {        // A diagonal ray crosses up to 3 cells per block travelled, so a walk counted in
        // iterations starves rays that travel further (shoulder camera) or aim steeply.
        // Capped walks therefore count cells generously and stop on travelled distance
        // instead; the uncapped walk is first person exactly as it always was. tMax holds
        // the distance to the next cell boundary per axis, so its minimum is where the
        // current cell ends.
        var currentPosition = origin.Floor();
        var step = direction.Sign();
        var tDelta = Vector3.Abs(Vector3.One / direction);
        var dist = Vector3.Distance(Vector3.Zero, direction);
        var stepLimit = stopAtRange ? cellCap : range * dist;
        var lastStepNormal = Vector3.Zero;

        var tMax = new Vector3(
            (step.X > 0 ? currentPosition.X + 1 - origin.X : origin.X - currentPosition.X) * tDelta.X,
            (step.Y > 0 ? currentPosition.Y + 1 - origin.Y : origin.Y - currentPosition.Y) * tDelta.Y,
            (step.Z > 0 ? currentPosition.Z + 1 - origin.Z : origin.Z - currentPosition.Z) * tDelta.Z
        );

        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;
        hitBoxIndex = 0;
        hitT = 0f;

        for (var i = 0; i < stepLimit; i++)
        {
            // currentPosition components are floored floats, so the int casts are exact
            var cellX = (int)currentPosition.X;
            var cellY = (int)currentPosition.Y;
            var cellZ = (int)currentPosition.Z;
            var cellId = chunkManager.GetBlockGlobal(cellX, cellY, cellZ);

            // A fluid is hit only for a caller that asked for it, and then only as a source: a
            // flowing skirt is aimed through (Minecraft's SOURCE_ONLY), and the cell the eye is
            // already inside is skipped so swimming never targets the water on the player's face.
            var targetsCell = cellId != 0 && (!BlockType.IsFluid(cellId)
                || (includeFluids && i > 0 && FluidState.IsSource(chunkManager.GetStateGlobal(cellX, cellY, cellZ))));

            if (targetsCell)
            {
                // Every cell aims at its drawn shape: tree wood at its node and tubes, layered
                // snow at its stack depth, a facing block at its turned boxes (see BlockShape)
                var boxes = BlockShape.BoxesAt(cellId, chunkManager, cellX, cellY, cellZ);
                if (boxes == null || boxes.Length == 0)
                {
                    hitPosition = currentPosition;
                    hitNormal = lastStepNormal == Vector3.Zero ? -direction.Sign() : lastStepNormal;
                    hitBoxIndex = 0;
                    if (!RayIntersectsBox(origin, direction, currentPosition, currentPosition + Vector3.One,
                            float.PositiveInfinity, out var cellT, out _))
                        cellT = 0f;
                    hitT = cellT;
                    return true;
                }

                var bestT = float.PositiveInfinity;
                var bestBox = -1;
                var bestNormal = Vector3.Zero;
                for (var b = 0; b < boxes.Length; b++)
                {
                    var boxMin = currentPosition + boxes[b].Min;
                    var boxMax = currentPosition + boxes[b].Max;
                    if (!RayIntersectsBox(origin, direction, boxMin, boxMax, range, out var t, out var normal))
                        continue;
                    if (t < bestT)
                    {
                        bestT = t;
                        bestBox = b;
                        bestNormal = normal;
                    }
                }

                if (bestBox >= 0)
                {
                    hitPosition = currentPosition;
                    hitBoxIndex = bestBox;
                    hitT = bestT;
                    // The eye inside the box has no entry face: fall back to the cell entry.
                    hitNormal = bestNormal == Vector3.Zero
                        ? (lastStepNormal == Vector3.Zero ? -direction.Sign() : lastStepNormal)
                        : bestNormal;
                    return true;
                }
            }

            if (stopAtRange && Math.Min(tMax.X, Math.Min(tMax.Y, tMax.Z)) > range)
                break;

            if (tMax.X < tMax.Y && tMax.X < tMax.Z)
            {
                currentPosition.X += step.X;
                tMax.X += tDelta.X;
                lastStepNormal = new Vector3(-step.X, 0, 0);
            }
            else if (tMax.Y < tMax.Z)
            {
                currentPosition.Y += step.Y;
                tMax.Y += tDelta.Y;
                lastStepNormal = new Vector3(0, -step.Y, 0);
            }
            else
            {
                currentPosition.Z += step.Z;
                tMax.Z += tDelta.Z;
                lastStepNormal = new Vector3(0, 0, -step.Z);
            }
        }

        return false;
    }

    internal static bool RayIntersectsBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max,
        float maxDistance, out float tEnter, out Vector3 normal)
    {
        tEnter = 0f;
        normal = Vector3.Zero;
        var tExit = float.PositiveInfinity;
        var hitAxis = -1;

        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }

            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            var tNear = MathF.Min(t1, t2);
            var tFar = MathF.Max(t1, t2);
            if (tNear > tEnter || (tNear == tEnter && hitAxis < 0))
            {
                tEnter = tNear;
                hitAxis = axis;
            }
            tExit = MathF.Min(tExit, tFar);
            if (tEnter > tExit) return false;
        }

        if (tExit < 0f || tEnter > maxDistance) return false;
        if (tEnter < 0f)
        {
            tEnter = 0f;
            normal = Vector3.Zero;
            return true;
        }

        var sign = (hitAxis == 0 ? direction.X : hitAxis == 1 ? direction.Y : direction.Z) > 0f ? -1f : 1f;
        normal = hitAxis switch
        {
            0 => new Vector3(sign, 0f, 0f),
            1 => new Vector3(0f, sign, 0f),
            _ => new Vector3(0f, 0f, sign),
        };
        return true;
    }
}
