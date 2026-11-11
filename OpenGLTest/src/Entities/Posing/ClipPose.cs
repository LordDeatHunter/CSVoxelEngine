namespace VoxelEngine.Entities.Posing;

/// <summary>
/// One clip sampled at one time, as a rig pose: what a thing shows that plays its clips one at a time with no
/// blending (a chest's lid). The default value, with no clip, is the rest pose.
/// </summary>
/// <param name="Clip">The clip being shown, or null for the rest pose.</param>
/// <param name="Time">Seconds into the clip.</param>
public readonly record struct ClipPose(AnimationClip? Clip, float Time) : IBonePose
{
    /// <inheritdoc/>
    public AnimationClip.BoneTransform LocalTransform(string bone) =>
        Clip?.Sample(bone, Time) ?? AnimationClip.BoneTransform.Identity;
}
