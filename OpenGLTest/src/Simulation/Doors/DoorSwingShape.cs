using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;

namespace VoxelEngine.Simulation.Doors;

/// <summary>
/// GL-free geometry of a side-hinged door's swing. The shut panel (the block's closed boxes, authored facing north)
/// turns a quarter turn about a vertical pin through the middle of its hinge-side end, which carries it exactly onto
/// the open boxes; <see cref="PanelMatrix"/> is that turn at any point of the swing, placed in the world, and what
/// both the draw (<c>DoorPasses</c>) and the push (<see cref="TryPush"/>) are built on, so a body is moved by the
/// panel the player sees.
/// </summary>
public static class DoorSwingShape
{
    /// <summary>The bounds of the shut panel of <paramref name="type"/> in its authored cell (facing north, hinged
    /// left), which a right-hand hinge shares: the mirror of a panel spanning the cell is the same box.</summary>
    public static (Vector3 Min, Vector3 Max) ClosedBounds(BlockType type) =>
        (type.Hitboxes.Select(b => b.Min).Aggregate(Vector3.Min), type.Hitboxes.Select(b => b.Max).Aggregate(Vector3.Max));

    /// <summary>
    /// The transform from the authored shut panel (cell-local, facing north; the mirrored model for a right-hand
    /// hinge) to the world, for a door in <paramref name="cell"/> swinging to <paramref name="state"/> and standing
    /// <paramref name="open"/> (0 shut to 1 open) of the way.
    /// </summary>
    public static Matrix4x4 PanelMatrix(BlockType type, Vector3 cell, byte state, float open)
    {
        var (min, max) = ClosedBounds(type);
        var halfThickness = (max.Z - min.Z) * 0.5f;
        var hingeRight = DoorState.HingeRight(state);
        // The pin stands half a panel thickness in from the hinge-side end, so the turned panel stays in the cell
        var pin = new Vector3(hingeRight ? max.X - halfThickness : min.X + halfThickness, 0f, (min.Z + max.Z) * 0.5f);
        var swing = (hingeRight ? -1f : 1f) * open * MathF.PI * 0.5f;
        var center = new Vector3(0.5f, 0f, 0.5f);
        return Matrix4x4.CreateTranslation(-pin) * Matrix4x4.CreateRotationY(swing) * Matrix4x4.CreateTranslation(pin)
               * Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationY(FacingAngle(DoorState.Facing(state)))
               * Matrix4x4.CreateTranslation(center + cell);
    }

    // The turn about the cell's middle that HorizontalFacing.RotatePoint makes for each facing
    private static float FacingAngle(int facing) => facing switch
    {
        1 => MathF.PI,
        2 => MathF.PI * 0.5f,
        3 => -MathF.PI * 0.5f,
        _ => 0f,
    };

    /// <summary>
    /// The shove a swinging door gives an upright body of <paramref name="radius"/> centered at
    /// <paramref name="bodyCenter"/> and spanning <paramref name="minY"/>..<paramref name="maxY"/>: none unless the
    /// body touches the panel on the side it is moving toward, else the horizontal move that carries it just clear
    /// of that face, capped at <see cref="DoorConfig.MaxPushBlocks"/>. On the swing's last tick
    /// (<see cref="DoorSwing.Done"/>) a body on the other side is cleared too, the nearer way out, so nothing is
    /// left inside the panel when it turns back into a block.
    /// </summary>
    public static bool TryPush(in DoorSwing swing, Vector3 bodyCenter, float radius, float minY, float maxY, out Vector3 delta)
    {
        delta = default;
        if (BlockType.ById[swing.LowerId] is not { } type) return false;
        // Both halves swing as one panel two cells tall
        if (maxY <= swing.Cell.Y || minY >= swing.Cell.Y + 2f) return false;

        var toWorld = PanelMatrix(type, swing.Cell, swing.State, swing.Open);
        if (!Matrix4x4.Invert(toWorld, out var toPanel)) return false;
        var local = Vector3.Transform(bodyCenter, toPanel);
        var (min, max) = ClosedBounds(type);
        var nearestX = Math.Clamp(local.X, min.X, max.X);
        var nearestZ = Math.Clamp(local.Z, min.Z, max.Z);
        var dx = local.X - nearestX;
        var dz = local.Z - nearestZ;
        if (dx * dx + dz * dz >= radius * radius) return false;

        // An opening panel leads with its inner face (north, -Z in its own frame), a closing one with its outer
        var middle = (min.Z + max.Z) * 0.5f;
        var side = swing.Opening ? -1f : 1f;
        var ahead = (local.Z - middle) * side;
        if (ahead < 0f)
        {
            if (!swing.Done) return false;
            side = -side;
            ahead = -ahead;
        }

        var push = MathF.Min((max.Z - min.Z) * 0.5f + radius - ahead, DoorConfig.MaxPushBlocks);
        if (push <= 0f) return false;
        delta = Vector3.TransformNormal(new Vector3(0f, 0f, side * push), toWorld);
        return true;
    }
}
