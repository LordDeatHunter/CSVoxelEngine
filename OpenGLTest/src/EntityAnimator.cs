using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Plays an entity's Blockbench keyframe clips (<see cref="AnimationClip"/>) on its rig, one
/// instance per entity. Two layers: a <b>locomotion</b> layer that crossfades the looping
/// <see cref="IdleClip"/> and <see cref="WalkClip"/> by horizontal speed (walk playback rate
/// scales with that speed), and a one-shot <b>action</b> layer (<see cref="Play"/>) that
/// overrides only the bones its clip keys, blending in and out over
/// <see cref="ActionBlendSeconds"/> so an action starts and ends exactly on the locomotion pose.
/// Timeline events crossed by an action's playback are queued for <see cref="ConsumeEvent"/>,
/// which is how gameplay learns the frame a stone leaves the hand or a swing lands.
/// GL-free; allocates nothing per frame after construction.
/// </summary>
public sealed class EntityAnimator
{
    /// <summary>Name of the looping clip played while standing.</summary>
    public const string IdleClip = "idle";

    /// <summary>Name of the looping clip played while moving.</summary>
    public const string WalkClip = "walk";

    /// <summary><see cref="MathUtils.Damp"/> rate of the idle/walk crossfade, per second.</summary>
    public const float WalkBlendRate = 8f;

    /// <summary>Horizontal speed (blocks/s) above which the entity counts as walking.</summary>
    public const float MovingSpeedThreshold = 0.05f;

    /// <summary>Seconds an action takes to blend in at its start and out at its end.</summary>
    public const float ActionBlendSeconds = 0.1f;

    // Below this weight the walk clip no longer shows, so its clock may stop.
    private const float WalkClockMinBlend = 0.01f;
    // A fading walk keeps stepping at least this fraction of its authored rate.
    private const float MinWalkRate = 0.5f;
    // Longest parent chain composed before a cycle is assumed.
    private const int MaxChainDepth = 16;

    private readonly AnimationClip? _idle;
    private readonly AnimationClip? _walk;
    private readonly List<string> _pendingEvents = new(4);

    private float _idleTime;
    private float _walkTime;
    private float _walkBlend;
    private AnimationClip? _action;
    private float _actionTime;
    private float _lastActionTime;
    private AnimationClip? _overlay;
    private float _overlayTime;

    /// <param name="clips">Every clip the model authored; idle and walk are found by name.</param>
    /// <param name="walkReferenceSpeed">Horizontal speed (blocks/s) at which the walk clip plays
    /// at its authored rate.</param>
    public EntityAnimator(IReadOnlyList<AnimationClip> clips, float walkReferenceSpeed = 3f)
    {
        Clips = clips;
        WalkReferenceSpeed = walkReferenceSpeed > 0f ? walkReferenceSpeed : 1f;
        _idle = Find(IdleClip);
        _walk = Find(WalkClip);
    }

    /// <summary>Every clip this animator can play, in the model's file order.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>Horizontal speed at which the walk clip plays at its authored rate.</summary>
    public float WalkReferenceSpeed { get; }

    /// <summary>True while any one-shot action is playing.</summary>
    public bool IsActionPlaying => _action != null;

    /// <summary>How far the running action is through its clip, 0..1; 0 when none plays.</summary>
    public float ActionProgress =>
        _action == null || _action.Length <= 0f ? 0f : _actionTime / _action.Length;

    /// <summary>
    /// How strongly the running action overrides locomotion on the bones it keys: eased up from 0
    /// over the first <see cref="ActionBlendSeconds"/> and back to 0 over the last, so the action
    /// reproduces the base pose exactly at both ends. 0 with no action or a zero-length clip.
    /// </summary>
    public float ActionWeight
    {
        get
        {
            if (_action == null || _action.Length <= 0f) return 0f;
            var edge = MathF.Min(_actionTime, _action.Length - _actionTime) / ActionBlendSeconds;
            return MathUtils.SmoothStep(MathF.Min(edge, 1f));
        }
    }

    /// <summary>True while an additive overlay (<see cref="PlayOverlay"/>) is running.</summary>
    public bool IsOverlayPlaying => _overlay != null;

    /// <summary>Whether the model authored a clip with that name.</summary>
    public bool HasClip(string name) => Find(name) != null;

    /// <summary>
    /// Starts a one-shot <b>overlay</b> (a flinch): played on top of whatever locomotion and action
    /// are showing, adding its translation and rotating the pose by its rotation on the bones it
    /// keys, so a hit never cancels a throw or swing already under way. Authored to start and end
    /// at the identity, so it needs no blend. Replaying restarts it. False when there is no such clip.
    /// </summary>
    public bool PlayOverlay(string clipName)
    {
        var clip = Find(clipName);
        if (clip == null) return false;
        _overlay = clip;
        _overlayTime = 0f;
        return true;
    }

    /// <summary>The clip with that name (ordinal, ignoring case), or null.</summary>
    public AnimationClip? Find(string name)
    {
        for (var i = 0; i < Clips.Count; i++)
            if (string.Equals(Clips[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return Clips[i];
        return null;
    }

    /// <summary>
    /// Advances both layers by <paramref name="deltaTime"/>. The idle clock always runs; the walk
    /// clock runs while the walk still shows, at a rate proportional to
    /// <paramref name="horizontalSpeed"/> (never below half the authored rate, so a fading walk
    /// keeps stepping). Events the action crosses this frame are queued before the action ends.
    /// </summary>
    public void Update(float deltaTime, float horizontalSpeed)
    {
        if (deltaTime <= 0f) return;

        var moving = horizontalSpeed > MovingSpeedThreshold;
        _walkBlend = MathUtils.Damp(_walkBlend, moving ? 1f : 0f, WalkBlendRate, deltaTime);
        _idleTime += deltaTime;
        if (_overlay != null)
        {
            _overlayTime += deltaTime;
            if (_overlayTime >= _overlay.Length) _overlay = null;
        }
        if (_walkBlend > WalkClockMinBlend)
            _walkTime += deltaTime * MathF.Max(horizontalSpeed / WalkReferenceSpeed, MinWalkRate);

        if (_action == null) return;
        var from = _lastActionTime;
        _actionTime = MathF.Min(_actionTime + deltaTime, _action.Length);
        var events = _action.Events;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Time > from && e.Time <= _actionTime)
                _pendingEvents.Add(e.Name);
        }
        _lastActionTime = _actionTime;
        if (_actionTime >= _action.Length)
            _action = null;
    }

    /// <summary>
    /// Starts the one-shot clip <paramref name="clipName"/>. Returns false when no such clip
    /// exists or that same clip is already playing (the running play is left untouched). A
    /// different action replaces the running one outright and its blend-in restarts from the
    /// locomotion pose; undelivered events of the replaced play are dropped.
    /// </summary>
    public bool Play(string clipName)
    {
        if (IsPlaying(clipName)) return false;
        var clip = Find(clipName);
        if (clip == null) return false;
        _action = clip;
        _actionTime = 0f;
        // Below zero so an event authored at t = 0 still fires on the first Update.
        _lastActionTime = -1f;
        _pendingEvents.Clear();
        return true;
    }

    /// <summary>True while the one-shot clip with that name (ignoring case) is playing.</summary>
    public bool IsPlaying(string clipName) =>
        _action != null && string.Equals(_action.Name, clipName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True exactly once per fired event with that name (ignoring case): removes the first
    /// pending entry and reports it.
    /// </summary>
    public bool ConsumeEvent(string name)
    {
        for (var i = 0; i < _pendingEvents.Count; i++)
        {
            if (!string.Equals(_pendingEvents[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            _pendingEvents.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>Discards every pending event, for a viewer that plays clips for show.</summary>
    public void DrainEvents() => _pendingEvents.Clear();

    /// <summary>
    /// The bone's animated local transform (relative to its rest pivot): idle and walk blended
    /// by the walk weight (translation and scale lerped, rotation slerped; a missing clip gives
    /// identity), then, only on a bone the running action keys, blended toward the action's
    /// sample by <see cref="ActionWeight"/>.
    /// </summary>
    public AnimationClip.BoneTransform LocalTransform(string bone)
    {
        var idle = _idle?.Sample(bone, _idleTime) ?? AnimationClip.BoneTransform.Identity;
        var walk = _walk?.Sample(bone, _walkTime) ?? AnimationClip.BoneTransform.Identity;
        var pose = Blend(idle, walk, _walkBlend);
        if (_action != null && _action.Keys(bone))
            pose = Blend(pose, _action.Sample(bone, _actionTime), ActionWeight);
        if (_overlay != null && _overlay.Keys(bone))
        {
            var add = _overlay.Sample(bone, _overlayTime);
            pose = new AnimationClip.BoneTransform(pose.Translation + add.Translation,
                Quaternion.Normalize(add.Rotation * pose.Rotation), pose.Scale);
        }
        return pose;
    }

    /// <summary>
    /// World matrix of <paramref name="part"/>: its animated local transform composed down the
    /// outliner's parent chain onto a root placed at <paramref name="feet"/> and turned by
    /// <paramref name="facingYaw"/> (radians about +Y; the model faces -Z at 0). Per link,
    /// scale, rotate, then translate by the pivot offset from the parent plus the animated
    /// translation, then the parent — System.Numerics is row-vector, so the leftmost factor runs
    /// first. The part's cubes are pivot-relative, so this is their model matrix. An unknown part
    /// or a parent cycle yields identity.
    /// </summary>
    public Matrix4x4 GetPartMatrix(string part, PlayerRig rig, Vector3 feet, float facingYaw)
    {
        if (!rig.Parts.ContainsKey(part)) return Matrix4x4.Identity;
        var root = Matrix4x4.CreateRotationY(facingYaw) * Matrix4x4.CreateTranslation(feet);
        return TryCompose(part, rig, root, 0, out var matrix) ? matrix : Matrix4x4.Identity;
    }

    /// <summary>
    /// Anchor for an item held by <paramref name="part"/>: the part's matrix moved down to the
    /// bottom of its lowest cube (the <see cref="PlayerModel.HandDepth"/> rule, 0 for a part
    /// without cubes), so a held item follows the swing of that limb.
    /// </summary>
    public Matrix4x4 HandAnchor(string part, PlayerRig rig, Vector3 feet, float facingYaw)
    {
        var depth = 0f;
        if (rig.Parts.TryGetValue(part, out var p))
            foreach (var cube in p.Cubes)
                depth = MathF.Min(depth, cube.Min.Y);
        return Matrix4x4.CreateTranslation(0f, depth, 0f) * GetPartMatrix(part, rig, feet, facingYaw);
    }

    private bool TryCompose(string name, PlayerRig rig, Matrix4x4 root, int depth,
        out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (depth > MaxChainDepth) return false;
        var part = rig.Parts[name];

        Matrix4x4 parentMatrix;
        Vector3 parentPivot;
        if (part.Parent != null && rig.Parts.TryGetValue(part.Parent, out var parent))
        {
            if (!TryCompose(part.Parent, rig, root, depth + 1, out parentMatrix)) return false;
            parentPivot = parent.Pivot;
        }
        else
        {
            parentMatrix = root;
            parentPivot = Vector3.Zero;
        }

        var tr = LocalTransform(name);
        var local = Matrix4x4.CreateScale(tr.Scale)
            * Matrix4x4.CreateFromQuaternion(tr.Rotation)
            * Matrix4x4.CreateTranslation(part.Pivot - parentPivot + tr.Translation);
        matrix = local * parentMatrix;
        return true;
    }

    private static AnimationClip.BoneTransform Blend(AnimationClip.BoneTransform a,
        AnimationClip.BoneTransform b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        return new AnimationClip.BoneTransform(
            Vector3.Lerp(a.Translation, b.Translation, t),
            Quaternion.Slerp(a.Rotation, b.Rotation, t),
            Vector3.Lerp(a.Scale, b.Scale, t));
    }
}
