using System;
using System.Numerics;

namespace VoxelEngine.Player.Posing;

/// <summary>The arm-frame builders (an abduction, flexion and yaw frame for the upper arm, a flexion and bend frame for the forearm, the weighted blend) and the shoulder socket's slide toward the torso.</summary>
internal static class LimbFrames
{
    // The shoulder joint sits on the torso's side face (the arm's pivot is ShoulderX - the torso's half width = 0.1
    // outside it, so a hanging arm is flush). Swing the arm up and across and it would hinge on that outer point with a
    // gap opening against the body, so the joint slides in toward the torso as the arm lifts off hanging
    // (SocketInward at about 70 degrees), keeping the top of the arm seated on the body.
    internal const float ShoulderSocketInward = 0.09f;

    // How far an arm has lifted off hanging, 0..1 (smooth over about 0.15 to 1.2 radians), from its upper-arm frame.
    internal static float SocketWeight(Quaternion upper)
    {
        var axis = Vector3.Transform(new Vector3(0f, -1f, 0f), upper);
        var lift = MathF.Acos(Math.Clamp(-axis.Y, -1f, 1f));
        return MathUtils.SmoothStep(0.15f, 1.2f, lift);
    }

    // ---- Arm frames ----------------------------------------------------------------------
    // The base arm is a forward flex about X plus a yaw about the vertical: both of its bones stay in
    // one plane, so it cannot reach out to the side. Poses that need that (the airborne arms, the
    // punch's flared elbow, the throw's cock, the swim stroke) are instead given as an abduction
    // (roll about the body's front axis), a flexion about X and a yaw, and the forearm as a flexion
    // plus a bend about the roll's own axis, then slerped over the base by a weight. Roll first, then
    // flexion, then yaw (System.Numerics is row-vector, so the leftmost factor runs first): with the
    // arm held out to the side the elbow hinge (X) is the vertical axis, so a plain forearm flexion
    // swings it forward in the horizontal plane, and a bend about Z lifts it up.
    internal static Quaternion FrameOf(Matrix4x4 m) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));

    // side is +1 for the right arm and -1 for the left, which mirrors the roll and the yaw.
    internal static Quaternion UpperFrame(float side, float roll, float flex, float yaw) =>
        FrameOf(Matrix4x4.CreateRotationZ(side * roll) * Matrix4x4.CreateRotationX(flex)
                * Matrix4x4.CreateRotationY(side * yaw));

    internal static Quaternion ForeFrame(float side, float flex, float bend) =>
        FrameOf(Matrix4x4.CreateRotationX(flex) * Matrix4x4.CreateRotationZ(side * bend));

    internal static void Blend(ref Quaternion frame, Quaternion target, float weight)
    {
        if (weight > 0.001f) frame = Quaternion.Slerp(frame, target, MathUtils.Clamp01(weight));
    }
}
