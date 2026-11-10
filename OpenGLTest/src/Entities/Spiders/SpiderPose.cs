using System.Numerics;
using VoxelEngine.Entities.Posing;

namespace VoxelEngine.Entities.Spiders;

/// <summary>
/// A spider's pose: its clips for the body, and for the legs the rotations <see cref="SpiderLegs"/> solved against
/// the world. A leg bone the running action keys (the raised front legs of the bite, every curled leg of the
/// death) is handed back to the clip by the action's weight, so the action blends in and out of the planted pose.
/// </summary>
public readonly struct SpiderPose(EntityAnimator animation, SpiderLegs legs) : IBonePose
{
    /// <inheritdoc/>
    public AnimationClip.BoneTransform LocalTransform(string bone)
    {
        var clip = animation.LocalTransform(bone);
        if (!legs.TryGetRotation(bone, out var solved)) return clip;

        var weight = animation.ActionKeys(bone) ? animation.ActionWeight : 0f;
        if (weight >= 1f) return clip;
        return new AnimationClip.BoneTransform(clip.Translation * weight,
            Quaternion.Slerp(solved, clip.Rotation, weight), Vector3.One);
    }
}
