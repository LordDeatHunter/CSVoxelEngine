using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Posing;

/// <summary>
/// Composes a pose (<see cref="IBonePose"/>) down a Blockbench rig's parent chain into the world matrix of one
/// part: the one definition of how a rig's groups stack, shared by the mobs' <see cref="EntityAnimator"/> and the
/// single-clip poses (<see cref="ClipPose"/>). GL-free and allocation-free.
/// </summary>
public static class RigPose
{
    // Longest parent chain composed before a cycle is assumed.
    private const int MaxChainDepth = 16;

    /// <summary>
    /// World matrix of <paramref name="part"/> under <paramref name="pose"/>: its animated local transform
    /// composed down the outliner's parent chain onto <paramref name="root"/> (where the model's origin stands and
    /// which way it faces). Per link, scale, rotate, then translate by the pivot offset from the parent plus the
    /// animated translation, then the parent: System.Numerics is row-vector, so the leftmost factor runs first.
    /// The part's cubes are pivot-relative, so this is their model matrix. An unknown part or a parent cycle
    /// yields identity.
    /// </summary>
    public static Matrix4x4 PartMatrix<TPose>(TPose pose, string part, PlayerRig rig, in Matrix4x4 root)
        where TPose : IBonePose
    {
        if (!rig.Parts.ContainsKey(part)) return Matrix4x4.Identity;
        return TryCompose(pose, part, rig, root, 0, out var matrix) ? matrix : Matrix4x4.Identity;
    }

    private static bool TryCompose<TPose>(TPose pose, string name, PlayerRig rig, in Matrix4x4 root, int depth,
        out Matrix4x4 matrix) where TPose : IBonePose
    {
        matrix = Matrix4x4.Identity;
        if (depth > MaxChainDepth) return false;
        var part = rig.Parts[name];

        Matrix4x4 parentMatrix;
        Vector3 parentPivot;
        if (part.Parent != null && rig.Parts.TryGetValue(part.Parent, out var parent))
        {
            if (!TryCompose(pose, part.Parent, rig, root, depth + 1, out parentMatrix)) return false;
            parentPivot = parent.Pivot;
        }
        else
        {
            parentMatrix = root;
            parentPivot = Vector3.Zero;
        }

        var tr = pose.LocalTransform(name);
        var local = Matrix4x4.CreateScale(tr.Scale)
            * Matrix4x4.CreateFromQuaternion(tr.Rotation)
            * Matrix4x4.CreateTranslation(part.Pivot - parentPivot + tr.Translation);
        matrix = local * parentMatrix;
        return true;
    }
}
