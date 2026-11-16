using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Items;

namespace VoxelEngine.Player;

/// <summary>
/// The held item's sizing and the one definition of its model matrix, GL-free. The game's held item
/// (<c>PlayerPasses.RenderHeldItem</c>), the slice trail and DevStudio all pose the held shape
/// through this class, so none of them can drift from what is drawn.
/// </summary>
public static class HeldItemPose
{
    // Held-item sizing, in blocks. The sprite is unit-sized and the cube is full-block;
    // both are shrunk to sit in the palm. The grips lift the shape just clear of the
    // forearm with a nudge outward (+X, away from the body) and forward (-Z).
    // RenderManager re-exports them so DevStudio can show and tune the same defaults
    // the game ships.
    internal const float HeldItemScale = 0.5f;
    internal static readonly Vector3 HeldItemGrip = new(0.03f, 0.10f, -0.04f);
    internal const float HeldBlockScale = 0.38f;
    internal static readonly Vector3 HeldBlockGrip = new(0.08f, 0.10f, -0.02f);

    /// <summary>
    /// The held-item model matrix: uniform scale, the item's display transform, the grip nudge,
    /// then the hand anchor. Shared by the game's held item and DevStudio so the
    /// preview can never drift from what the game draws. Blocks pass
    /// <see cref="Matrix4x4.Identity"/> as <paramref name="displayTransform"/>: their shape is
    /// the block's own baked model, not a display-configured sprite.
    /// </summary>
    internal static Matrix4x4 Pose(Matrix4x4 hand, Matrix4x4 displayTransform, float scale, Vector3 grip) =>
        Matrix4x4.CreateScale(scale) * displayTransform * Matrix4x4.CreateTranslation(grip) * hand;

    /// <summary>
    /// The model matrix the held sprite of <paramref name="type"/> is drawn with in
    /// <paramref name="player"/>'s right hand (the left one when <paramref name="offhand"/>): the hand anchor at the given eye, look and first-person
    /// back offset, the item's display transform with the pose's rotation offset, and the shipped
    /// sprite scale and grip. The one definition of the drawn sprite's matrix.
    /// </summary>
    internal static Matrix4x4 SpriteMatrix(PlayerModel player, Item type, Vector3 eye, float yawDeg,
        float pitchDeg, float backOffset, bool offhand = false)
    {
        var hand = offhand ? player.GetOffhandAnchorMatrix(eye, yawDeg, backOffset, pitchDeg)
            : player.GetHandAnchorMatrix(eye, yawDeg, backOffset, pitchDeg);
        var held = Pose(hand, type.DisplayMatrix(player.HeldItemRotationOffset), HeldItemScale, HeldItemGrip);
        if (!type.ThrowAligned) return held;

        var aim = Vector3Utils.FromYawPitch(yawDeg, pitchDeg);
        return Aimed(held, hand, player.ThrowAimWeight(offhand), aim, HeldItemScale * type.DisplayScale, HeldItemGrip,
            FirstPersonAimShift(Vector3.Transform(HeldItemGrip, hand) - eye, aim, backOffset));
    }

    /// <summary>
    /// Where a wound-up item is moved to in first person, for a palm at <paramref name="palmFromEye"/> and the unit
    /// <paramref name="aim"/>: the cocked hand is beside and behind the eye, where a level javelin would sit wholly out
    /// of view, so it is slid <see cref="AimedFirstPersonReach"/> along the aim and pulled
    /// <see cref="AimedFirstPersonPull"/> of the way onto the line of sight, bringing its front half into the frame.
    /// Scaled by the first-person body's back shift (<paramref name="backOffset"/>), so it is zero from the shoulder
    /// camera and for a remote player, and fades with the glide between the two views.
    /// </summary>
    internal static Vector3 FirstPersonAimShift(Vector3 palmFromEye, Vector3 aim, float backOffset)
    {
        var firstPerson = MathUtils.Clamp01(backOffset / PlayerModel.FirstPersonBodyBack);
        var offAxis = palmFromEye - aim * Vector3.Dot(palmFromEye, aim);
        return (aim * AimedFirstPersonReach - offAxis * AimedFirstPersonPull) * firstPerson;
    }

    /// <summary>Blocks a wound-up item is slid along the aim in first person (<see cref="FirstPersonAimShift"/>).</summary>
    internal const float AimedFirstPersonReach = 0.45f;

    /// <summary>The share of its distance from the line of sight a wound-up item is pulled in by in first person.</summary>
    internal const float AimedFirstPersonPull = 0.45f;

    /// <summary>
    /// A point-first throwable (<see cref="Item.ThrowAligned"/>) wound up for a throw: <paramref name="held"/>,
    /// its resting matrix, turned by <paramref name="weight"/> (0 at rest, 1 fully cocked) to lie along
    /// <paramref name="aim"/>, the thrower's view, tip first, exactly as it will fly
    /// (<see cref="ThrownProjectile.AlignedRotation"/>), turning about the palm of <paramref name="hand"/>: the
    /// spot of the item the hand grips at rest (where its <c>display</c> puts the palm) stays in the palm.
    /// So a javelin is carried level over the shoulder and leaves the hand already pointing where it goes,
    /// whatever the arm is doing. <paramref name="scale"/> is the item's drawn size in the hand, and
    /// <paramref name="shift"/> moves it that far at full weight (<see cref="FirstPersonAimShift"/>).
    /// </summary>
    internal static Matrix4x4 Aimed(in Matrix4x4 held, in Matrix4x4 hand, float weight, Vector3 aim, float scale,
        Vector3 grip, Vector3 shift = default)
    {
        if (weight <= 0f || !Matrix4x4.Decompose(held, out _, out var rest, out _)
                         || !Matrix4x4.Invert(held, out var toItem)) return held;

        var palm = Vector3.Transform(grip, hand);
        var gripped = Vector3.Transform(palm, toItem);
        var aimed = Quaternion.CreateFromRotationMatrix(ThrownProjectile.AlignedRotation(aim));
        var turned = Matrix4x4.CreateScale(scale)
                     * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(rest, aimed, weight));
        return turned * Matrix4x4.CreateTranslation(palm - Vector3.Transform(gripped, turned) + shift * weight);
    }

    /// <summary>A lantern hangs upright below the hand's grip, regardless of the wrist's rotation. The left hand
    /// (<paramref name="offhand"/>) mirrors the grip and the tilt so the lantern hangs inward of that arm too.</summary>
    internal static Matrix4x4 LanternMatrix(Matrix4x4 hand, float yawDeg, float scale = HeldBlockScale, bool offhand = false)
    {
        var side = offhand ? -1f : 1f;
        var grip = Vector3.Transform(new Vector3(-0.06f * side, 0.02f, -0.10f), hand);
        // Pivot at the chain's top. A small outward tilt clears the forearm while the grip stays in the fingers.
        return Matrix4x4.CreateTranslation(0f, -.5f, 0f) * Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationZ(LanternConfig.HangTilt * side)
            * Matrix4x4.CreateRotationY(-MathUtils.ToRadians(yawDeg + 90f)) * Matrix4x4.CreateTranslation(grip);
    }

    /// <summary>The first-person body's back shift at a shoulder-camera blend (0 first person, 1 shoulder).</summary>
    internal static float BackOffset(float shoulderBlend) => PlayerModel.FirstPersonBodyBack * (1f - shoulderBlend);
}
