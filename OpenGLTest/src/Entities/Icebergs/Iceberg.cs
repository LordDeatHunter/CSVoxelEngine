using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Entities.Icebergs;

/// <summary>
/// One drifting iceberg: a variant's <see cref="IcebergShape"/> placed at a waterline center with
/// a horizontal scale and a quarter-turn yaw, both fixed for life. Not a <see cref="Mob"/>: it has
/// no health, is never a target and only moves horizontally (the center's Y is fixed for life, the
/// bob and tilt are render-only, see <see cref="IcebergWobble"/>). The world-space collision boxes
/// are cached and rebuilt only by <see cref="SetCenter"/>, the one path that moves the center. A
/// client's copy is a mirror (<see cref="Mirror"/>): it never collides and only dead-reckons along
/// the server's velocity for a short lead between snapshots. Main thread only, GL-free.
/// </summary>
public sealed class Iceberg
{
    private readonly List<(Vector3 Min, Vector3 Max)> _boxes = [];
    private Vector3 _velocity;
    private float _sinceSnapshot;

    /// <summary>A fresh spawn: it rises out of the sea over <see cref="IcebergConfig.EmergeSeconds"/>.</summary>
    public Iceberg(IcebergShape shape, int variant, float scale, int turns, int seed, Vector3 center, float lifeSeconds)
    {
        Shape = shape;
        Variant = variant;
        Scale = scale;
        Turns = turns & 3;
        Seed = seed;
        Mass = shape.Mass(scale);
        LifeLeft = lifeSeconds;
        SetCenter(center);
        LastCarriedCenter = center;
    }

    /// <summary>
    /// A client's display-only copy of a server berg. It starts fully emerged, so a berg already
    /// afloat never rises out of the sea when a client joins or streams it in.
    /// </summary>
    public static Iceberg Mirror(IcebergShape shape, int variant, float scale, int turns, int seed,
        Vector3 center, Vector3 velocity, float sink01)
    {
        var berg = new Iceberg(shape, variant, scale, turns, seed, center, 0f)
        {
            IsMirror = true,
            Emerge01 = 1f,
        };
        berg.MirrorSetState(center, velocity, sink01);
        return berg;
    }

    /// <summary>The collision shape of the berg's variant, at model scale.</summary>
    public IcebergShape Shape { get; }

    /// <summary>Index of the variant (size class) in the entity's variant list.</summary>
    public int Variant { get; }

    /// <summary>Horizontal scale, fixed for life; the height never scales.</summary>
    public float Scale { get; }

    /// <summary>Quarter turns about the vertical axis, 0..3, fixed for life.</summary>
    public int Turns { get; }

    /// <summary>Seed of the berg's drift spread and wobble.</summary>
    public int Seed { get; }

    /// <summary>The waterline center in world space; its Y never changes.</summary>
    public Vector3 Center { get; private set; }

    /// <summary>Horizontal velocity in blocks per second; Y is always 0.</summary>
    public Vector3 Velocity
    {
        get => _velocity;
        set => _velocity = value with { Y = 0f };
    }

    /// <summary>The berg's mass at its scale (<see cref="IcebergShape.Mass"/>), cached.</summary>
    public float Mass { get; }

    /// <summary>Seconds of life left before the berg starts sinking.</summary>
    public float LifeLeft { get; set; }

    /// <summary>How far the berg has risen out of the sea, 0..1. Render only.</summary>
    public float Emerge01 { get; private set; }

    /// <summary>How far the berg has sunk away at the end of its life, 0..1.</summary>
    public float Sink01 { get; private set; }

    /// <summary>True once the berg has started sinking; it no longer collides from then on.</summary>
    public bool IsSinking { get; private set; }

    /// <summary>True once the berg has sunk away completely and can be dropped.</summary>
    public bool IsRemovable => Sink01 >= 1f;

    /// <summary>The center the rider carry last saw; the manager advances it after each carry.</summary>
    public Vector3 LastCarriedCenter { get; set; }

    /// <summary>True for a client's display-only copy, which never simulates or collides.</summary>
    public bool IsMirror { get; private init; }

    /// <summary>Whether bodies, terrain and other bergs collide with this berg: until it sinks.</summary>
    public bool CollidesWithBodies => !IsSinking;

    /// <summary>The placed collision boxes in world space, rebuilt by <see cref="SetCenter"/>.</summary>
    public IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes => _boxes;

    /// <summary>The union of <see cref="Boxes"/> in world space.</summary>
    public (Vector3 Min, Vector3 Max) Bounds { get; private set; }

    /// <summary>
    /// How far below its waterline the renderer draws the berg, in blocks: all the way under at
    /// the start of its emergence and at the end of its sinking, nothing while it floats.
    /// </summary>
    public float VisualSinkDepth =>
        MathF.Max(0f, (1f - Emerge01 + Sink01) * (Shape.Freeboard + 1f));

    /// <summary>Moves the berg to <paramref name="center"/> and rebuilds its world-space boxes.</summary>
    public void SetCenter(Vector3 center)
    {
        Center = center;
        _boxes.Clear();
        Shape.PlaceAll(Turns, Scale, center, _boxes);
        Bounds = Shape.PlacedBounds(Turns, Scale, center);
    }

    /// <summary>Advances the rise out of the sea by <paramref name="dt"/> seconds.</summary>
    public void AdvanceEmerge(float dt) =>
        Emerge01 = MathF.Min(1f, Emerge01 + MathF.Max(0f, dt) / IcebergConfig.EmergeSeconds);

    /// <summary>Starts the end-of-life sinking; from then on the berg no longer collides.</summary>
    public void BeginSinking() => IsSinking = true;

    /// <summary>Advances the sinking by <paramref name="dt"/> seconds, once it has begun.</summary>
    public void AdvanceSink(float dt)
    {
        if (!IsSinking)
            return;
        Sink01 = MathF.Min(1f, Sink01 + MathF.Max(0f, dt) / IcebergConfig.SinkSeconds);
    }

    /// <summary>
    /// Applies a server snapshot to a mirror: the center, velocity and sinking snap to it and the
    /// dead-reckoning lead starts over.
    /// </summary>
    public void MirrorSetState(Vector3 center, Vector3 velocity, float sink01)
    {
        SetCenter(center);
        Velocity = velocity;
        Sink01 = Math.Clamp(sink01, 0f, 1f);
        IsSinking = Sink01 > 0f;
        _sinceSnapshot = 0f;
    }

    /// <summary>
    /// Dead-reckons a mirror along its last velocity, but never more than
    /// <see cref="IcebergConfig.MirrorMaxLeadSeconds"/> past its snapshot, and never collides.
    /// </summary>
    public void AdvanceMirror(float dt)
    {
        if (dt <= 0f)
            return;
        var lead = MathF.Min(dt, IcebergConfig.MirrorMaxLeadSeconds - _sinceSnapshot);
        _sinceSnapshot += dt;
        if (lead <= 0f || _velocity == Vector3.Zero)
            return;
        SetCenter(Center + _velocity * lead);
    }
}
