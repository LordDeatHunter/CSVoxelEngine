namespace VoxelEngine.Entities.Posing;

/// <summary>
/// A pose of a Blockbench rig: each bone's animated local transform, relative to its rest pivot. An
/// <see cref="EntityAnimator"/> is one (its blended layers) and so is a single clip at one time
/// (<see cref="ClipPose"/>); <see cref="RigPose"/> composes either down the rig's parent chain.
/// </summary>
public interface IBonePose
{
    /// <summary>The bone's animated local transform; identity for a bone the pose does not move.</summary>
    AnimationClip.BoneTransform LocalTransform(string bone);
}
