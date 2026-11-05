using System;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Player;

namespace VoxelEngine.Particles;

/// <summary>
/// The path one swinging blade's tip has drawn: a ring of committed samples plus a live head, which
/// <see cref="SliceTrailRibbon"/> turns into the glowing slice-trail ribbon. GL-free, main thread
/// only, and allocation-free after construction.
/// <para>
/// <b>Owner frame.</b> Every point is stored in the swinging player's owner frame: the eye
/// translation plus the drawn body yaw (<see cref="OwnerFrameAt"/>), and is turned back into the world
/// through <see cref="OwnerFrame"/> at build time. So the arc stays on the blade while the player walks
/// or turns, a lagging body (the shoulder view, a remote player) keeps it in hand, and camera motion
/// alone never adds a sample.
/// </para>
/// <para>
/// <b>Time.</b> Ages run on the world clock passed to <see cref="Update"/>, never on the swing's
/// progress, so the trail keeps fading while a hit-stop holds the swing at its impact pose.
/// </para>
/// <para>
/// <b>Rate.</b> VSync is off, so frame rates in the hundreds are normal: a sample is committed at most
/// once every <see cref="ParticleConfig.SliceTrailMinSampleSeconds"/> and only after
/// <see cref="ParticleConfig.SliceTrailMinStepBlocks"/> of travel, which keeps the trail's whole life
/// inside the ring at any rate. The live head moves on every update, so the newest end of the ribbon
/// always sits on the drawn tip.
/// </para>
/// <para>
/// <b>Speed.</b> A sample's strength (the ribbon's width) comes from the tip's speed measured over the
/// time it actually spent moving since the last commit: an update whose tip stands (nearly) still, or
/// one inside a hit-stop hold, does not count, so a hold neither commits samples nor dips the width
/// after it.
/// </para>
/// <para>
/// <b>Arc length.</b> Each sample carries the owner-frame tip arc length from its segment's first
/// sample, fixed when it is committed, so the coordinate of a surviving sample never changes as older
/// samples are pruned and the ribbon's streaks stay put on the arc.
/// </para>
/// A new segment starts whenever the swing restarts (its progress falls), so a slice and its return,
/// or two sword swings, are never bridged.
/// </summary>
public sealed class SliceTrail
{
    private const int Capacity = ParticleConfig.SliceTrailMaxSamples;

    private readonly Vector3[] _tip = new Vector3[Capacity];
    private readonly Vector3[] _inner = new Vector3[Capacity];
    private readonly double[] _time = new double[Capacity];
    private readonly float[] _strength = new float[Capacity];
    private readonly int[] _segment = new int[Capacity];
    private readonly float[] _along = new float[Capacity];
    private int _start;
    private int _count;

    // The live head: the newest tip, not yet committed while _headPending
    private Vector3 _headTip;
    private Vector3 _headInner;
    private double _headTime;
    private float _headStrength;
    private float _headAlong;
    private bool _headLive;
    private bool _headPending;

    // The last sample committed in the current segment, kept apart from the ring so pruning never loses it
    private Vector3 _lastCommitTip;
    private double _lastCommitTime;
    private float _lastCommitStrength;
    private float _lastCommitAlong;

    private int _currentSegment;
    private bool _emitting;
    private float _lastProgress;
    private double _lastUpdateTime;
    private Vector3 _lastHeadTip;
    private double _movingSeconds;

    /// <summary>The owner frame (local to world) the samples were last fed in; the ribbon is built through it.</summary>
    public Matrix4x4 OwnerFrame { get; private set; } = Matrix4x4.Identity;

    /// <summary>The sRGB color the ribbon is drawn with (the blade's <c>trail.color</c>).</summary>
    public Rgba Color { get; set; }

    /// <summary>Samples to read, oldest first: the committed ones, then the live head while it is not committed.</summary>
    public int Count => _count + (_headLive && _headPending ? 1 : 0);

    /// <summary>The committed samples alone, the live head left out.</summary>
    internal int CommittedCount => _count;

    /// <summary>
    /// The owner frame of a player whose eye is <paramref name="eye"/> and whose body is drawn at
    /// <paramref name="bodyYawDeg"/>: the same yaw mapping <c>BodyPartMatrices</c> poses the body with
    /// (a rotation of <c>-(yaw + 90)</c> degrees about Y), then the eye. Callers pass
    /// <c>model.DrawnBodyYaw(yaw)</c>, so a lagging body keeps the arc on its blade.
    /// </summary>
    public static Matrix4x4 OwnerFrameAt(Vector3 eye, float bodyYawDeg) =>
        Matrix4x4.CreateRotationY(-(bodyYawDeg + 90f) * MathF.PI / 180f) * Matrix4x4.CreateTranslation(eye);

    /// <summary>Whether <paramref name="model"/> is playing a blade swing (a slice or a sword swing).</summary>
    public static bool IsBladeSwing(PlayerModel model) => model.IsSlicing || model.IsSwordSwinging;

    /// <summary>The playing blade swing's progress (0 at the wind-up), or 1 with none playing.</summary>
    public static float SwingProgress(PlayerModel model) =>
        model.IsSlicing ? model.SliceProgress : model.IsSwordSwinging ? model.SwordProgress : 1f;

    /// <summary>
    /// A point on the held sprite's tip diagonal: <paramref name="reach"/> -1 is the hilt corner, 1 the
    /// tip corner, in the unit sprite's own space (transform it by the sprite's model matrix).
    /// </summary>
    public static Vector3 BladePoint(float reach) => new(0.5f * reach, 0.5f * reach, 0f);

    /// <summary>
    /// Feeds one update. <paramref name="now"/> is the world clock in seconds,
    /// <paramref name="swinging"/> whether a blade swing is playing (<see cref="IsBladeSwing"/>),
    /// <paramref name="swingProgress"/> its progress (<see cref="SwingProgress"/>),
    /// <paramref name="holding"/> whether a hit-stop holds it (<see cref="PlayerModel.IsHitStopped"/>),
    /// <paramref name="bladeMatrix"/> the drawn sprite's model matrix and <paramref name="ownerFrame"/>
    /// the swinging player's owner frame (<see cref="OwnerFrameAt"/>).
    /// </summary>
    public void Update(double now, bool swinging, float swingProgress, bool holding, in Matrix4x4 bladeMatrix,
        in Matrix4x4 ownerFrame)
    {
        OwnerFrame = ownerFrame;
        var dt = _emitting ? now - _lastUpdateTime : 0.0;
        _lastUpdateTime = now;
        Prune(now);

        if (!swinging)
        {
            FinishSegment();
            _headLive = false;
            _emitting = false;
            return;
        }

        if (!Matrix4x4.Invert(ownerFrame, out var toLocal))
            return;
        var tip = Vector3.Transform(Vector3.Transform(BladePoint(ParticleConfig.SliceTrailTipReach), bladeMatrix), toLocal);
        var inner = Vector3.Transform(Vector3.Transform(BladePoint(ParticleConfig.SliceTrailInnerReach), bladeMatrix), toLocal);

        if (!_emitting || swingProgress < _lastProgress - 1e-4f)
        {
            StartSegment(now, tip, inner, swingProgress);
            return;
        }

        var moved = Vector3.Distance(tip, _lastHeadTip);
        if (!holding && moved / Math.Max(dt, 1e-6) >= ParticleConfig.SliceTrailStillBlocksPerSecond)
            _movingSeconds += dt;

        MoveHead(now, tip, inner);
        var step = Vector3.Distance(_headTip, _lastCommitTip);
        if (!holding && now - _lastCommitTime >= ParticleConfig.SliceTrailMinSampleSeconds
            && step >= ParticleConfig.SliceTrailMinStepBlocks)
        {
            Commit();
            _movingSeconds = 0.0;
        }

        _lastHeadTip = tip;
        _lastProgress = swingProgress;
        _headLive = true;
    }

    /// <summary>Owner-frame tip of sample <paramref name="i"/> (0 the oldest).</summary>
    public Vector3 TipAt(int i) => IsHead(i) ? _headTip : _tip[Slot(i)];

    /// <summary>Owner-frame inner edge point of sample <paramref name="i"/>.</summary>
    public Vector3 InnerAt(int i) => IsHead(i) ? _headInner : _inner[Slot(i)];

    /// <summary>World-clock time sample <paramref name="i"/> was taken at.</summary>
    public double TimeAt(int i) => IsHead(i) ? _headTime : _time[Slot(i)];

    /// <summary>0..1 speed strength of sample <paramref name="i"/>: how wide the ribbon is there.</summary>
    public float StrengthAt(int i) => IsHead(i) ? _headStrength : _strength[Slot(i)];

    /// <summary>The swing (segment) sample <paramref name="i"/> belongs to; equal ids are one run.</summary>
    public int SegmentAt(int i) => IsHead(i) ? _currentSegment : _segment[Slot(i)];

    /// <summary>Owner-frame tip arc length, in blocks, from sample <paramref name="i"/>'s segment start.</summary>
    public float AlongAt(int i) => IsHead(i) ? _headAlong : _along[Slot(i)];

    /// <summary>Forgets every sample and the swing in progress; keeps <see cref="Color"/>.</summary>
    public void Clear()
    {
        _start = 0;
        _count = 0;
        _headLive = false;
        _headPending = false;
        _currentSegment = 0;
        _emitting = false;
        _lastProgress = 0f;
        _lastUpdateTime = 0.0;
        _lastHeadTip = default;
        _movingSeconds = 0.0;
        _lastCommitTip = default;
        _lastCommitTime = 0.0;
        _lastCommitStrength = 0f;
        _lastCommitAlong = 0f;
        OwnerFrame = Matrix4x4.Identity;
    }

    private bool IsHead(int i) => i == _count;

    private int Slot(int i) => (_start + i) % Capacity;

    // Drops committed samples past their life, oldest first
    private void Prune(double now)
    {
        while (_count > 0 && now - _time[_start] > ParticleConfig.SliceTrailLifeSeconds)
        {
            _start = (_start + 1) % Capacity;
            _count--;
        }
    }

    // Ends the segment in progress: its last head lands as a sample unless it sits on the last one
    private void FinishSegment()
    {
        if (!_headLive || !_headPending)
            return;
        if (Vector3.Distance(_headTip, _lastCommitTip) >= ParticleConfig.SliceTrailMinStepBlocks)
            Commit();
    }

    private void StartSegment(double now, Vector3 tip, Vector3 inner, float swingProgress)
    {
        FinishSegment();
        _currentSegment++;
        _movingSeconds = 0.0;
        _headTip = tip;
        _headInner = inner;
        _headTime = now;
        _headStrength = 0f;
        _headAlong = 0f;
        _headPending = true;
        Commit();
        _emitting = true;
        _headLive = true;
        _lastHeadTip = tip;
        _lastProgress = swingProgress;
    }

    private void MoveHead(double now, Vector3 tip, Vector3 inner)
    {
        var step = Vector3.Distance(tip, _lastCommitTip);
        _headTip = tip;
        _headInner = inner;
        _headTime = now;
        _headAlong = _lastCommitAlong + step;
        _headStrength = _movingSeconds > 0.0
            ? MathUtils.SmoothStep(ParticleConfig.SliceTrailMinSpeed, ParticleConfig.SliceTrailFullSpeed,
                (float)(step / Math.Max(_movingSeconds, 1e-4)))
            : _lastCommitStrength;
        _headPending = true;
    }

    // Commits the head as the newest sample, overwriting the oldest when the ring is full
    private void Commit()
    {
        if (_count == Capacity)
        {
            _start = (_start + 1) % Capacity;
            _count--;
        }
        var slot = Slot(_count);
        _tip[slot] = _headTip;
        _inner[slot] = _headInner;
        _time[slot] = _headTime;
        _strength[slot] = _headStrength;
        _segment[slot] = _currentSegment;
        _along[slot] = _headAlong;
        _count++;
        _headPending = false;
        _lastCommitTip = _headTip;
        _lastCommitTime = _headTime;
        _lastCommitStrength = _headStrength;
        _lastCommitAlong = _headAlong;
    }
}
