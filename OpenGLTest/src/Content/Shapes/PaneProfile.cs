using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// The dimensions of one <see cref="BlockRenderer.Pane"/> block, read from its <c>pane</c> JSON:
/// how wide its full-height center post is, how wide its arms are, and the height bands
/// (<see cref="Rails"/>) each arm fills - one full-height band for a glass pane or a wall, two
/// rails for a fence. Everything is cell-local (0..1), centered on the cell; an arm is never wider
/// than the post. Immutable and interned by <see cref="Create"/>, so two blocks with the same
/// dimensions share one instance, which is what <see cref="PaneShape.ConnectsTo(PaneProfile, byte, byte)"/>
/// compares: panes of one shape join each other, a fence never reaches into a glass pane. The
/// boxes of every arm mask are built once here. GL-free.
/// </summary>
public sealed class PaneProfile
{
    /// <summary>Pixels per block side, the unit of the JSON dimensions.</summary>
    public const int PixelsPerBlock = 16;

    /// <summary>The glass pane's shape, and every pane block's that declares no <c>pane</c>
    /// object: a 2 px post, 2 px arms, filling the full height.</summary>
    public static readonly PaneProfile Default = new(2, 2, [(0, PixelsPerBlock)]);

    // Every profile handed out so far beyond the default, so equal dimensions share an instance
    private static readonly List<PaneProfile> Interned = [];
    private static readonly object InternLock = new();

    private readonly BlockHitbox[][] _boxesByMask;

    private PaneProfile(int postPx, int armPx, (int Bottom, int Top)[] railsPx)
    {
        PostPx = postPx;
        ArmPx = armPx;
        RailsPx = railsPx;
        PostNear = (PixelsPerBlock - postPx) / 2f / PixelsPerBlock;
        PostFar = 1f - PostNear;
        ArmNear = (PixelsPerBlock - armPx) / 2f / PixelsPerBlock;
        ArmFar = 1f - ArmNear;
        Rails = new (float Bottom, float Top)[railsPx.Length];
        for (var i = 0; i < railsPx.Length; i++)
            Rails[i] = (railsPx[i].Bottom / (float)PixelsPerBlock, railsPx[i].Top / (float)PixelsPerBlock);
        FullArms = armPx == postPx && railsPx.Length == 1 && railsPx[0] == (0, PixelsPerBlock);
        MaxQuads = 6 + PaneShape.Directions * Rails.Length * 4;
        _boxesByMask = BuildAllBoxes();
    }

    /// <summary>The post's width in pixels.</summary>
    public int PostPx { get; }

    /// <summary>Every arm's width in pixels.</summary>
    public int ArmPx { get; }

    /// <summary>The height bands of every arm in pixels, ascending and apart.</summary>
    public IReadOnlyList<(int Bottom, int Top)> RailsPx { get; }

    /// <summary>The post's near and far edge across the cell, on both horizontal axes.</summary>
    public float PostNear { get; }

    /// <inheritdoc cref="PostNear"/>
    public float PostFar { get; }

    /// <summary>An arm's near and far edge across its own length.</summary>
    public float ArmNear { get; }

    /// <inheritdoc cref="ArmNear"/>
    public float ArmFar { get; }

    /// <summary>The height bands every arm fills, cell-local, ascending and apart.</summary>
    public (float Bottom, float Top)[] Rails { get; }

    /// <summary>Whether an arm covers the post's whole side (as wide as the post and full
    /// height), so the post drops that side and its boxes merge with the post's.</summary>
    public bool FullArms { get; }

    /// <summary>Most quads one pane of this shape emits: the post's six faces plus two walls, a top
    /// and a bottom per rail of each of the four arms.</summary>
    public int MaxQuads { get; }

    /// <summary>
    /// The profile with these dimensions (pixels): <paramref name="postPx"/> 1..16,
    /// <paramref name="armPx"/> 1..post, <paramref name="railsPx"/> a non-empty list of
    /// <c>(bottom, top)</c> bands inside 0..16, ascending and apart. Equal dimensions return the
    /// same instance. Throws <see cref="ArgumentException"/> on anything else.
    /// </summary>
    public static PaneProfile Create(int postPx, int armPx, IReadOnlyList<(int Bottom, int Top)> railsPx)
    {
        if (postPx is < 1 or > PixelsPerBlock)
            throw new ArgumentException($"post {postPx} px; expected 1..{PixelsPerBlock}.");
        if (armPx < 1 || armPx > postPx)
            throw new ArgumentException($"arm {armPx} px; expected 1..the post's {postPx}.");
        if (railsPx.Count == 0)
            throw new ArgumentException("no rails; an arm needs at least one height band.");
        for (var i = 0; i < railsPx.Count; i++)
        {
            var (bottom, top) = railsPx[i];
            if (bottom < 0 || top > PixelsPerBlock || bottom >= top)
                throw new ArgumentException($"rail [{bottom}, {top}]; expected 0 <= bottom < top <= {PixelsPerBlock}.");
            if (i > 0 && bottom <= railsPx[i - 1].Top)
                throw new ArgumentException($"rail [{bottom}, {top}] does not start above the rail before it.");
        }

        var rails = new (int, int)[railsPx.Count];
        for (var i = 0; i < rails.Length; i++) rails[i] = railsPx[i];
        if (Default.Matches(postPx, armPx, rails)) return Default;
        lock (InternLock)
        {
            foreach (var profile in Interned)
                if (profile.Matches(postPx, armPx, rails))
                    return profile;
            var created = new PaneProfile(postPx, armPx, rails);
            Interned.Add(created);
            return created;
        }
    }

    private bool Matches(int postPx, int armPx, (int Bottom, int Top)[] rails)
    {
        if (postPx != PostPx || armPx != ArmPx || rails.Length != RailsPx.Count) return false;
        for (var i = 0; i < rails.Length; i++)
            if (rails[i] != RailsPx[i]) return false;
        return true;
    }

    /// <summary>The full-height center post, cell-local.</summary>
    public void PostBox(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(PostNear, 0f, PostNear);
        max = new Vector3(PostFar, 1f, PostFar);
    }

    /// <summary>The arm from the post to the cell edge toward <paramref name="dir"/> over the
    /// height band <paramref name="rail"/>, cell-local.</summary>
    public void ArmBox(int dir, int rail, out Vector3 min, out Vector3 max)
    {
        var (bottom, top) = Rails[rail];
        (min, max) = dir switch
        {
            0 => (new Vector3(ArmNear, bottom, 0f), new Vector3(ArmFar, top, PostNear)),
            1 => (new Vector3(ArmNear, bottom, PostFar), new Vector3(ArmFar, top, 1f)),
            2 => (new Vector3(0f, bottom, ArmNear), new Vector3(PostNear, top, ArmFar)),
            _ => (new Vector3(PostFar, bottom, ArmNear), new Vector3(1f, top, ArmFar)),
        };
    }

    /// <summary>
    /// The selection and collision boxes of a pane of this shape with arm mask
    /// <paramref name="mask"/>, never overlapping, so their outline traces the drawn shape. With
    /// <see cref="FullArms"/> that is one bar along X through the post (out to the cell edge on
    /// each side with an arm) plus a north and a south stub; otherwise the post plus one box per
    /// arm and rail. The arrays are shared; never write into them.
    /// </summary>
    public BlockHitbox[] Boxes(int mask) => _boxesByMask[mask & 15];

    private BlockHitbox[][] BuildAllBoxes()
    {
        var all = new BlockHitbox[16][];
        for (var mask = 0; mask < 16; mask++)
            all[mask] = FullArms ? MergedBoxes(mask) : SplitBoxes(mask);
        return all;
    }

    private BlockHitbox[] MergedBoxes(int mask)
    {
        var bar = new BlockHitbox(
            new Vector3((mask & 1 << 2) != 0 ? 0f : PostNear, 0f, PostNear),
            new Vector3((mask & 1 << 3) != 0 ? 1f : PostFar, 1f, PostFar));
        var boxes = new BlockHitbox[1 + ((mask & 1) != 0 ? 1 : 0) + ((mask & 2) != 0 ? 1 : 0)];
        var next = 0;
        boxes[next++] = bar;
        for (var dir = 0; dir < 2; dir++)
        {
            if ((mask & 1 << dir) == 0) continue;
            ArmBox(dir, 0, out var min, out var max);
            boxes[next++] = new BlockHitbox(min, max);
        }
        return boxes;
    }

    private BlockHitbox[] SplitBoxes(int mask)
    {
        var boxes = new List<BlockHitbox>(1 + PaneShape.Directions * Rails.Length);
        PostBox(out var postMin, out var postMax);
        boxes.Add(new BlockHitbox(postMin, postMax));
        for (var dir = 0; dir < PaneShape.Directions; dir++)
        {
            if ((mask & 1 << dir) == 0) continue;
            for (var rail = 0; rail < Rails.Length; rail++)
            {
                ArmBox(dir, rail, out var min, out var max);
                boxes.Add(new BlockHitbox(min, max));
            }
        }
        return boxes.ToArray();
    }
}
