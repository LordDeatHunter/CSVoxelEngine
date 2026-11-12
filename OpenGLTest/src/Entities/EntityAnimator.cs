using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Player;

namespace VoxelEngine.Entities;

/// <summary>
/// Plays an entity's Blockbench keyframe clips (<see cref="AnimationClip"/>) on its rig, one
/// instance per entity. Two layers: a <b>locomotion</b> layer that crossfades the looping
/// <see cref="IdleClip"/> and a moving clip (<see cref="WalkClip"/>, or <see cref="FlyClip"/> for a
/// flyer) by the speed the owner hands it (horizontal for a walker, 3D for a flyer; the moving clip's
/// playback rate scales with it), and a one-shot <b>action</b> layer (<see cref="Play"/>) that
/// overrides only the bones its clip keys, blending in and out over
/// <see cref="ActionBlendSeconds"/> so an action starts and ends exactly on the locomotion pose.
/// Timeline events crossed by an action's playback are queued for <see cref="ConsumeEvent"/>,
/// which is how gameplay learns the frame a stone leaves the hand or a swing lands.
/// GL-free; allocates nothing per frame after construction.
/// </summary>
public sealed class EntityAnimator : IBonePose
{
    /// <summary>Name of the looping clip played while standing.</summary>
    public const string IdleClip = "idle";

    /// <summary>Name of the looping clip played while moving.</summary>
    public const string WalkClip = "walk";

    /// <summary>Name of the looping clip a flyer plays while moving, in place of <see cref="WalkClip"/>.</summary>
    public const string FlyClip = "fly";

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

    private AnimationClip? _idle;
    private AnimationClip? _walk;
    // The locomotion pair a gait change is fading out of, with its walk clock, and how far the fade is (1 = done)
    private AnimationClip? _fromIdle;
    private AnimationClip? _fromWalk;
    private float _fromWalkTime;
    private float _gaitBlend = 1f;
    private readonly List<string> _pendingEvents = new(4);

    private float _idleTime;
    private float _walkTime;
    private float _walkBlend;
    private AnimationClip? _action;
    private float _actionTime;
    private float _lastActionTime;
    private AnimationClip? _overlay;
    private float _overlayTime;

    /// <param name="clips">Every clip the model authored; idle and the moving clip are found by name.</param>
    /// <param name="walkReferenceSpeed">Speed (blocks/s) at which the moving clip plays at its
    /// authored rate.</param>
    /// <param name="moveClip">The clip crossfaded in while moving: <see cref="WalkClip"/>, or
    /// <see cref="FlyClip"/> for a flyer.</param>
    public EntityAnimator(IReadOnlyList<AnimationClip> clips, float walkReferenceSpeed = 3f, string moveClip = WalkClip)
    {
        Clips = clips;
        WalkReferenceSpeed = walkReferenceSpeed > 0f ? walkReferenceSpeed : 1f;
        _idle = Find(IdleClip);
        _walk = Find(moveClip);
    }

    /// <summary>Every clip this animator can play, in the model's file order.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>Speed at which the moving clip (walk or fly) plays at its authored rate.</summary>
    public float WalkReferenceSpeed { get; private set; }

    /// <summary>Seconds a gait change (<see cref="SetGait"/>) takes to fade from the old pair to the new.</summary>
    public const float GaitBlendSeconds = 0.2f;

    /// <summary>
    /// Switches the locomotion pair to the clips named <paramref name="idleClip"/> and <paramref name="moveClip"/>
    /// (a deer breaking from its walk into its run, a crow leaving the ground for its flight), the moving one
    /// authored at <paramref name="referenceSpeed"/>. The pose fades from the old pair to the new over
    /// <see cref="GaitBlendSeconds"/>; naming the pair already in use only updates the reference speed. A name
    /// the model lacks leaves that half of the pair empty (the rest pose).
    /// </summary>
    public void SetGait(string idleClip, string moveClip, float referenceSpeed)
    {
        var idle = Find(idleClip);
        var walk = Find(moveClip);
        WalkReferenceSpeed = referenceSpeed > 0f ? referenceSpeed : 1f;
        if (ReferenceEquals(idle, _idle) && ReferenceEquals(walk, _walk)) return;

        _fromIdle = _idle;
        _fromWalk = _walk;
        _fromWalkTime = _walkTime;
        _idle = idle;
        _walk = walk;
        _walkTime = 0f;
        _gaitBlend = 0f;
    }

    /// <summary>
    /// Ends the running action early: it blends back out to the locomotion pose over
    /// <see cref="ActionBlendSeconds"/> instead of playing to its end, and the events it had not reached
    /// never fire (a grazing deer that bolts). Does nothing with no action playing.
    /// </summary>
    public void CutAction()
    {
        if (_action == null) return;
        _actionTime = MathF.Max(_actionTime, _action.Length - ActionBlendSeconds);
        _lastActionTime = _actionTime;
    }

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

    /// <summary>Whether the running action keys <paramref name="bone"/>, and so overrides it by
    /// <see cref="ActionWeight"/>; false with no action playing.</summary>
    public bool ActionKeys(string bone) => _action != null && _action.Keys(bone);

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
    /// clock runs while the walk (or fly) clip still shows, at a rate proportional to
    /// <paramref name="speed"/> (never below half the authored rate, so a fading walk keeps
    /// stepping): a walker's horizontal speed, a flyer's 3D speed. Events the action crosses this
    /// frame are queued before the action ends.
    /// </summary>
    public void Update(float deltaTime, float speed)
    {
        if (deltaTime <= 0f) return;

        var moving = speed > MovingSpeedThreshold;
        _walkBlend = MathUtils.Damp(_walkBlend, moving ? 1f : 0f, WalkBlendRate, deltaTime);
        _idleTime += deltaTime;
        if (_overlay != null)
        {
            _overlayTime += deltaTime;
            if (_overlayTime >= _overlay.Length) _overlay = null;
        }
        if (_walkBlend > WalkClockMinBlend)
        {
            var step = deltaTime * MathF.Max(speed / WalkReferenceSpeed, MinWalkRate);
            _walkTime += step;
            _fromWalkTime += step;
        }
        if (_gaitBlend < 1f) _gaitBlend = MathF.Min(1f, _gaitBlend + deltaTime / GaitBlendSeconds);

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
        var pose = Locomotion(_idle, _walk, _walkTime, bone);
        if (_gaitBlend < 1f)
            pose = Blend(Locomotion(_fromIdle, _fromWalk, _fromWalkTime, bone), pose, MathUtils.SmoothStep(_gaitBlend));
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
    /// outliner's parent chain (<see cref="RigPose.PartMatrix"/>) onto a root placed at
    /// <paramref name="feet"/> and turned by <paramref name="facingYaw"/> (radians about +Y; the
    /// model faces -Z at 0). The part's cubes are pivot-relative, so this is their model matrix.
    /// An unknown part or a parent cycle yields identity.
    /// </summary>
    public Matrix4x4 GetPartMatrix(string part, PlayerRig rig, Vector3 feet, float facingYaw) =>
        RigPose.PartMatrix(this, part, rig, Matrix4x4.CreateRotationY(facingYaw) * Matrix4x4.CreateTranslation(feet));

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

    // One locomotion pair's pose for a bone: its idle and moving clips blended by the walk weight
    private AnimationClip.BoneTransform Locomotion(AnimationClip? idle, AnimationClip? walk, float walkTime, string bone) =>
        Blend(idle?.Sample(bone, _idleTime) ?? AnimationClip.BoneTransform.Identity,
            walk?.Sample(bone, walkTime) ?? AnimationClip.BoneTransform.Identity, _walkBlend);

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
