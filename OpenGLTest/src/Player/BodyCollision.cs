using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Player;

/// <summary>
/// The player-body collision rule and the stepped moves through it, split out of
/// <see cref="PlayerPhysics"/>: <see cref="BodyCollides"/> (shared with the server's
/// <c>MovementRules</c> and <see cref="PlayerBodyProbe"/>), the per-axis sweep
/// <see cref="MoveOnAxis"/> and the auto-step <see cref="TryStepUp"/>. State-free, GL-free,
/// main thread.
/// </summary>
internal static class BodyCollision
{
    private const float CollisionStepSize = 0.05f;
    // StepClearance is a hair of extra lift so a block top the feet land exactly on is not read as a
    // collision because of float rounding.
    private const float StepClearance = 0.001f;

    internal static Vector3? TryStepUp(Vector3 start, Vector3 frameMovement, float stepHeight,
        ChunkManager chunkManager)
    {
        if (stepHeight <= 0f)
            return null;

        var steps = Math.Max(1, (int)MathF.Ceiling(stepHeight / CollisionStepSize));
        for (var i = 1; i <= steps; i++)
        {
            var lift = MathF.Min(i * CollisionStepSize, stepHeight) + StepClearance;
            var lifted = OffsetAxis(start, 1, lift);
            if (BodyCollides(lifted, chunkManager))
                continue;

            var stepped = MoveOnAxis(lifted, frameMovement.X, 0, chunkManager, out var blockedX);
            stepped = MoveOnAxis(stepped, frameMovement.Z, 2, chunkManager, out var blockedZ);
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

    internal static Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunkManager, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = MathUtils.ClampStep(remaining, CollisionStepSize);
            var candidate = OffsetAxis(position, axis, step);

            if (BodyCollides(candidate, chunkManager))
            {
                collided = true;
                break;
            }

            position = candidate;
            remaining -= step;
        }

        return position;
    }

    private static Vector3 OffsetAxis(Vector3 value, int axis, float delta)
    {
        return axis switch
        {
            0 => new Vector3(value.X + delta, value.Y, value.Z),
            1 => new Vector3(value.X, value.Y + delta, value.Z),
            2 => new Vector3(value.X, value.Y, value.Z + delta),
            _ => value
        };
    }

    /// <summary>
    /// Whether the body box at <paramref name="eyePosition"/> overlaps a solid or collidesByHitbox
    /// cell. Player body only: drops, slimes and falling blocks still see the full cell. The rule
    /// has no simulation state, so the server can judge a connected player's body
    /// (<see cref="PlayerBodyProbe"/>) by the very rule the client's own physics moves it under.
    /// An unloaded cell reads as air.
    /// </summary>
    public static bool BodyCollides(Vector3 eyePosition, ChunkManager chunkManager)
    {
        var min = new Vector3(
            eyePosition.X - PlayerPhysics.PlayerRadius,
            eyePosition.Y - PlayerPhysics.EyeHeight,
            eyePosition.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(
            eyePosition.X + PlayerPhysics.PlayerRadius,
            eyePosition.Y - PlayerPhysics.EyeHeight + PlayerPhysics.PlayerHeight,
            eyePosition.Z + PlayerPhysics.PlayerRadius);

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
            // Tree wood collides with its drawn node-and-tubes shape and a collidesByHitbox
            // block (a mold slab) with its declared hitboxes, so the body slips past thin limbs,
            // under high branches and onto a low slab; every other solid block keeps the
            // full-cube test it always had.
            if (!BlockType.IsTreeWood(blockId) && !BlockType.CollidesByHitboxOf(blockId)) return true;
            if (TreeWoodShape.OverlapsShape(blockId, chunkManager, x, y, z, min, max))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The shove behind <see cref="Camera.PushOutOf"/>: pushes <paramref name="camera"/>'s body out of a falling cube along the smallest-penetration axis that does not park the eye inside a solid (see <see cref="BodyCollides"/>).
    /// </summary>
    internal static void PushOutOf(Camera camera, Vector3 center, float halfExtent, ChunkManager chunkManager)
    {
        if (camera.IsFlying) return;

        var min = new Vector3(
            camera.Position.X - PlayerPhysics.PlayerRadius,
            camera.Position.Y - Camera.EyeHeight,
            camera.Position.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(
            camera.Position.X + PlayerPhysics.PlayerRadius,
            camera.Position.Y - Camera.EyeHeight + PlayerPhysics.PlayerHeight,
            camera.Position.Z + PlayerPhysics.PlayerRadius);
        var eMin = center - new Vector3(halfExtent);
        var eMax = center + new Vector3(halfExtent);

        var ox = Math.Min(max.X, eMax.X) - Math.Max(min.X, eMin.X);
        var oy = Math.Min(max.Y, eMax.Y) - Math.Max(min.Y, eMin.Y);
        var oz = Math.Min(max.Z, eMax.Z) - Math.Max(min.Z, eMin.Z);
        if (ox <= 0f || oy <= 0f || oz <= 0f) return;

        // Signed shove per axis that moves the body fully clear of the cube; ties go negative.
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

            if (BodyCollides(candidate, chunkManager)) continue;

            camera.SetPosition(candidate);
            return;
        }
    }
}
