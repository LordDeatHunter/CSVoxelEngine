using System.Numerics;
using VoxelEngine.Items;

namespace VoxelEngine.Player;

/// <summary>
/// The held item's sizing and the one definition of its model matrix, GL-free. The game's held item
/// (<c>PlayerPasses.RenderHeldItem</c>), the slice trail and the item debugger all pose the held shape
/// through this class, so none of them can drift from what is drawn.
/// </summary>
public static class HeldItemPose
{
    // Held-item sizing, in blocks. The sprite is unit-sized and the cube is full-block;
    // both are shrunk to sit in the palm. The grips lift the shape just clear of the
    // forearm with a nudge outward (+X, away from the body) and forward (-Z).
    // RenderManager re-exports them so the item debugger can show and tune the same defaults
    // the game ships.
    internal const float HeldItemScale = 0.5f;
    internal static readonly Vector3 HeldItemGrip = new(0.03f, 0.10f, -0.04f);
    internal const float HeldBlockScale = 0.38f;
    internal static readonly Vector3 HeldBlockGrip = new(0.08f, 0.10f, -0.02f);

    /// <summary>
    /// The held-item model matrix: uniform scale, the item's display transform, the grip nudge,
    /// then the hand anchor. Shared by the game's held item and the item debugger so the
    /// preview can never drift from what the game draws. Blocks pass
    /// <see cref="Matrix4x4.Identity"/> as <paramref name="displayTransform"/>: their shape is
    /// the block's own baked model, not a display-configured sprite.
    /// </summary>
    internal static Matrix4x4 Pose(Matrix4x4 hand, Matrix4x4 displayTransform, float scale, Vector3 grip) =>
        Matrix4x4.CreateScale(scale) * displayTransform * Matrix4x4.CreateTranslation(grip) * hand;

    /// <summary>
    /// The model matrix the held sprite of <paramref name="type"/> is drawn with in
    /// <paramref name="player"/>'s right hand: the hand anchor at the given eye, look and first-person
    /// back offset, the item's display transform with the pose's rotation offset, and the shipped
    /// sprite scale and grip. The one definition of the drawn sprite's matrix.
    /// </summary>
    internal static Matrix4x4 SpriteMatrix(PlayerModel player, Item type, Vector3 eye, float yawDeg,
        float pitchDeg, float backOffset) =>
        Pose(player.GetHandAnchorMatrix(eye, yawDeg, backOffset, pitchDeg),
            type.DisplayMatrix(player.HeldItemRotationOffset), HeldItemScale, HeldItemGrip);

    /// <summary>The first-person body's back shift at a shoulder-camera blend (0 first person, 1 shoulder).</summary>
    internal static float BackOffset(float shoulderBlend) => PlayerModel.FirstPersonBodyBack * (1f - shoulderBlend);
}
