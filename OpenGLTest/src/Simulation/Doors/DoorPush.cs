using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Doors;

/// <summary>
/// Moves the bodies standing in a swinging door's way (<see cref="DoorSwingShape.TryPush"/>): the local player on
/// the side that owns its position (singleplayer and every client), the mobs and the item drops on the side that
/// simulates them. Every move goes through the body's own collision, so a door never pushes anything into a wall;
/// a player pinned between the two is squeezed out of the arrived panel the shortest free way instead.
/// </summary>
internal static class DoorPush
{
    /// <summary>Moves the local player out of the way of every swinging door. Never while flying.</summary>
    internal static void PushPlayer(IReadOnlyList<DoorSwing> swings, Camera camera, ChunkManager chunks)
    {
        if (camera.IsFlying) return;
        foreach (var swing in swings)
        {
            var feet = camera.Position.Y - Camera.EyeHeight;
            if (DoorSwingShape.TryPush(swing, camera.Position, PlayerPhysics.PlayerRadius, feet,
                    feet + PlayerPhysics.PlayerHeight, out var delta))
                MovePlayer(camera, delta, chunks);
            if (swing.Done)
                SqueezeOut(swing, camera, chunks);
        }
    }

    /// <summary>Moves the mobs and the item drops out of the way of every swinging door.</summary>
    internal static void PushBodies(IReadOnlyList<DoorSwing> swings, IReadOnlyList<Mob> mobs,
        IReadOnlyList<ItemDrop> drops, ChunkManager chunks)
    {
        foreach (var swing in swings)
        {
            foreach (var mob in mobs)
                if (!mob.IsDying && DoorSwingShape.TryPush(swing, mob.Position, mob.BodyHalfExtent,
                        mob.Position.Y - mob.BodyHalfHeight, mob.Position.Y + mob.BodyHalfHeight, out var delta))
                    mob.Nudge(delta, chunks);

            foreach (var drop in drops)
                if (DoorSwingShape.TryPush(swing, drop.Position, ItemDrop.HalfExtent, drop.Position.Y - ItemDrop.HalfExtent,
                        drop.Position.Y + ItemDrop.HalfExtent, out var delta))
                    drop.Nudge(delta, chunks);
        }
    }

    // The whole shove when the body fits there, else whichever single axis of it does
    private static void MovePlayer(Camera camera, Vector3 delta, ChunkManager chunks)
    {
        foreach (var step in (ReadOnlySpan<Vector3>)[delta, delta with { Z = 0f }, delta with { X = 0f }])
        {
            if (step == Vector3.Zero || BodyCollision.BodyCollides(camera.Position + step, chunks)) continue;
            camera.SetPosition(camera.Position + step);
            return;
        }
    }

    // The panel has arrived and is about to collide as a block again: a body the shove could not clear (pinned
    // against a wall) leaves its boxes along whichever axis is free
    private static void SqueezeOut(in DoorSwing swing, Camera camera, ChunkManager chunks)
    {
        if (BlockType.ById[swing.LowerId] is not { RotatedHitboxes: { } shapes }) return;
        var boxes = shapes[BlockType.ShapeIndex(swing.State, shapes.Length)];
        for (var dy = 0; dy < 2; dy++)
        {
            var cell = swing.Cell + new Vector3(0f, dy, 0f);
            foreach (var box in boxes)
                BodyCollision.PushOutOfBox(camera, cell + box.Min, cell + box.Max, chunks);
        }
    }
}
