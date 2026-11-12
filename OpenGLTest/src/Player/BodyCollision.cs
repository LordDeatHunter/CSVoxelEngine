using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Entities.Icebergs;

namespace VoxelEngine.Player;

/// <summary>
/// The player-body collision rule and the stepped moves through it, split out of
/// <see cref="PlayerPhysics"/>: <see cref="BodyCollides"/> (shared with the server's
/// <c>MovementRules</c> and <see cref="PlayerBodyProbe"/>), the per-axis sweep
/// <see cref="MoveOnAxis"/> and the auto-step <see cref="TryStepUp"/>, stepping by <see cref="BoxSweep.StepSize"/> like every loose body. State-free, GL-free,
/// main thread.
/// </summary>
internal static class BodyCollision
{
    // StepClearance is a hair of extra lift so a block top the feet land exactly on is not read as a
    // collision because of float rounding.
    private const float StepClearance = 0.001f;

    internal static Vector3? TryStepUp(Vector3 start, Vector3 frameMovement, float stepHeight,
        ChunkManager chunkManager, IBodySolids? solids = null)
    {
        if (stepHeight <= 0f)
            return null;

        var steps = Math.Max(1, (int)MathF.Ceiling(stepHeight / BoxSweep.StepSize));
        for (var i = 1; i <= steps; i++)
        {
            var lift = MathF.Min(i * BoxSweep.StepSize, stepHeight) + StepClearance;
            var lifted = BoxSweep.OffsetAxis(start, 1, lift);
            if (BodyCollides(lifted, chunkManager, solids))
                continue;

            var stepped = MoveOnAxis(lifted, frameMovement.X, 0, chunkManager, out var blockedX, solids);
            stepped = MoveOnAxis(stepped, frameMovement.Z, 2, chunkManager, out var blockedZ, solids);
            if (blockedX || blockedZ)
                continue;

            // A lift that clears nothing must not teleport the body straight up; only accept
            // it when the horizontal move actually advanced past the start.
            if (MathF.Abs(stepped.X - start.X) < 1e-4f && MathF.Abs(stepped.Z - start.Z) < 1e-4f)
                continue;

            return stepped;
        }

        return null;
    }

    internal static Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunkManager, out bool collided,
        IBodySolids? solids = null)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = MathUtils.ClampStep(remaining, BoxSweep.StepSize);
            var candidate = BoxSweep.OffsetAxis(position, axis, step);

            if (BodyCollides(candidate, chunkManager, solids))
            {
                collided = true;
                break;
            }

            position = candidate;
            remaining -= step;
        }

        return position;
    }

    /// <summary>
    /// Whether the body box at <paramref name="eyePosition"/> overlaps a solid or collidesByHitbox
    /// cell, or the solid part of a sink block below its soft layer (<see cref="SinkLayer"/>). Player body only: drops, slimes and falling blocks still see the full cell. The rule
    /// has no simulation state, so the server can judge a connected player's body
    /// (<see cref="PlayerBodyProbe"/>) by the very rule the client's own physics moves it under.
    /// An unloaded cell reads as air. <paramref name="solids"/> adds the non-block solids (the
    /// iceberg boxes), tested after the cells.
    /// </summary>
    public static bool BodyCollides(Vector3 eyePosition, ChunkManager chunkManager, IBodySolids? solids = null)
    {
        var min = IcebergContact.BodyMin(eyePosition);
        var max = IcebergContact.BodyMax(eyePosition);

        VoxelCoords.CellRange(min, max, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var blockId = chunkManager.GetBlockGlobal(x, y, z);
            // A layered-snow cell only collides once its stack fills the cell; every other
            // block answers as before. See BlockType.IsSolidCell.
            if (!BlockType.IsSolidCell(blockId, chunkManager.GetStateGlobal(x, y, z))) continue;
            // A pass-through block (leaves) never blocks the body; it only scales the walk
            // while the body overlaps it (see BlockMoveFactor).
            if (BlockType.MovementSpeedOf(blockId) > 0f) continue;
            // A sink block (a cloud) collides only below its soft top layer, which the body sinks
            // into (SinkLayer)
            var sink = BlockType.SinkOf(blockId);
            if (sink > 0f)
            {
                if (min.Y < y + SinkLayer.SolidHeight(sink) && max.Y > y) return true;
                continue;
            }
            // Tree wood collides with its drawn node-and-tubes shape and a collidesByHitbox
            // block (a mold slab) with its declared hitboxes, so the body slips past thin limbs,
            // under high branches and onto a low slab; every other solid block keeps the
            // full-cube test it always had.
            if (!BlockType.IsTreeWood(blockId) && !BlockType.CollidesByHitboxOf(blockId)) return true;
            if (TreeWoodShape.OverlapsShape(blockId, chunkManager, x, y, z, min, max))
                return true;
        }

        return solids?.Overlaps(min, max) == true;
    }

    /// <summary>
    /// The shove behind <see cref="Camera.PushOutOf"/>: pushes <paramref name="camera"/>'s body out of a falling cube (see <see cref="PushOutOfBox"/>).
    /// </summary>
    internal static void PushOutOf(Camera camera, Vector3 center, float halfExtent, ChunkManager chunkManager) =>
        PushOutOfBox(camera, center - new Vector3(halfExtent), center + new Vector3(halfExtent), chunkManager, null);

    /// <summary>
    /// Pushes <paramref name="camera"/>'s body out of the box from <paramref name="eMin"/> to
    /// <paramref name="eMax"/> (a falling cube, an iceberg box) along the smallest-penetration axis
    /// that does not park the eye inside a solid (see <see cref="BodyCollides"/>, with
    /// <paramref name="solids"/>). Never while flying.
    /// </summary>
    internal static void PushOutOfBox(Camera camera, Vector3 eMin, Vector3 eMax, ChunkManager chunkManager,
        IBodySolids? solids)
    {
        if (camera.IsFlying) return;

        var min = IcebergContact.BodyMin(camera.Position);
        var max = IcebergContact.BodyMax(camera.Position);
        var center = (eMin + eMax) * 0.5f;

        var ox = Math.Min(max.X, eMax.X) - Math.Max(min.X, eMin.X);
        var oy = Math.Min(max.Y, eMax.Y) - Math.Max(min.Y, eMin.Y);
        var oz = Math.Min(max.Z, eMax.Z) - Math.Max(min.Z, eMin.Z);
        if (ox <= 0f || oy <= 0f || oz <= 0f) return;

        // Signed shove per axis that moves the body fully clear of the box; ties go negative.
        // Shallowest first, so the shove moves the player the least.
        var bodyCenterY = camera.Position.Y - Camera.EyeHeight + PlayerPhysics.PlayerHeight * 0.5f;
        Span<float> pushes = [
            camera.Position.X <= center.X ? eMin.X - max.X : eMax.X - min.X,
            bodyCenterY <= center.Y ? eMin.Y - max.Y : eMax.Y - min.Y,
            camera.Position.Z <= center.Z ? eMin.Z - max.Z : eMax.Z - min.Z,
        ];
        Span<int> order = [0, 1, 2];
        for (var a = 0; a < 2; a++)
        for (var b = a + 1; b < 3; b++)
            if (Math.Abs(pushes[order[b]]) < Math.Abs(pushes[order[a]]))
                (order[a], order[b]) = (order[b], order[a]);

        foreach (var axis in order)
        {
            var candidate = axis switch
            {
                0 => camera.Position + new Vector3(pushes[0], 0f, 0f),
                1 => camera.Position + new Vector3(0f, pushes[1], 0f),
                _ => camera.Position + new Vector3(0f, 0f, pushes[2]),
            };

            if (BodyCollides(candidate, chunkManager, solids)) continue;

            camera.SetPosition(candidate);
            return;
        }
    }
}
