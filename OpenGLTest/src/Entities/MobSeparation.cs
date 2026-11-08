using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities;

/// <summary>
/// The shared mob-to-mob body rule: living mobs whose body boxes overlap push each other apart
/// along the shallower horizontal overlap, so a pack can never collapse into one silhouette. A nudge
/// goes through the block sweep (<see cref="Mob.Nudge"/>), so it never shoves a body into a wall; a
/// dying mob and a boss are left alone, and two bodies only touch when every axis overlaps, so a
/// flyer above a walker is not pushed. Main thread only, in the authoritative simulation.
/// </summary>
internal static class MobSeparation
{
    // A hair past the touching position, so a resolved pair reads as clear on the next frame too.
    private const float Slack = 0.001f;

    /// <summary>Resolves every overlapping pair of <paramref name="mobs"/> in place.</summary>
    public static void Separate(IReadOnlyList<Mob> mobs, ChunkManager chunks)
    {
        for (var i = 0; i < mobs.Count; i++)
        {
            var a = mobs[i];
            if (!Pushes(a)) continue;
            for (var j = i + 1; j < mobs.Count; j++)
            {
                var b = mobs[j];
                if (Pushes(b)) SeparatePair(a, b, chunks);
            }
        }
    }

    // A dying body is already leaving, and a multi-part boss owns a footprint far larger than its
    // body box; shoving it out of a crowd would fight its own movement.
    private static bool Pushes(Mob mob) => !mob.IsDying && !mob.IsBoss;

    private static void SeparatePair(Mob a, Mob b, ChunkManager chunks)
    {
        var delta = a.Position - b.Position;
        if (MathF.Abs(delta.Y) >= a.BodyHalfHeight + b.BodyHalfHeight) return;

        var reach = a.BodyHalfExtent + b.BodyHalfExtent;
        var overlapX = reach - MathF.Abs(delta.X);
        if (overlapX <= 0f) return;
        var overlapZ = reach - MathF.Abs(delta.Z);
        if (overlapZ <= 0f) return;

        var alongX = overlapX <= overlapZ;
        var overlap = MathF.Min(overlapX, overlapZ) + Slack;
        var sign = (alongX ? delta.X : delta.Z) < 0f ? -1f : 1f;
        var push = alongX ? new Vector3(sign, 0f, 0f) : new Vector3(0f, 0f, sign);
        a.Nudge(push * (overlap * 0.5f), chunks);
        b.Nudge(-push * (overlap * 0.5f), chunks);

        // One body may have been against a wall and taken less than its half, so the other takes
        // the remainder: one pass clears the overlap unless both are wedged.
        var after = a.Position - b.Position;
        var remaining = reach - MathF.Abs(alongX ? after.X : after.Z);
        if (remaining <= 0f) return;
        b.Nudge(-push * remaining, chunks);
        after = a.Position - b.Position;
        remaining = reach - MathF.Abs(alongX ? after.X : after.Z);
        if (remaining > 0f) a.Nudge(push * remaining, chunks);
    }
}
