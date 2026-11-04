using System.Numerics;

namespace VoxelEngine.Player.Posing;

/// <summary>
/// What the right arm, the body and the held blade do on one frame of a blade swing (a slice or a
/// sword swing), as the pose builders consume it: <see cref="SliceMotion.FrameAt"/> and
/// <see cref="SwordMotion.FrameAt"/> each shape their own, and <see cref="BodyPartMatrices"/>,
/// <see cref="SecondaryMotion"/>, <see cref="ShoulderReach"/> and the held item's display offset
/// read only this, so a new swing is a new shape, never a new branch in the body code. The default
/// (<see cref="None"/>) is no swing: every term zero, which leaves the rest pose untouched.
/// </summary>
internal readonly struct BladeSwingFrame
{
    /// <summary>No swing in progress.</summary>
    public static readonly BladeSwingFrame None = default;

    /// <summary>
    /// How far the arm is into the swing's own pose, 0..1: the carrying pose is eased into
    /// <see cref="Shoulder"/> and <see cref="Elbow"/> by it, the blade's wrist carry takes over the
    /// held item's orientation by it, and the torso's twist is taken back out of the arm's yaw by it
    /// (the swing was authored in body space). 0 at both ends of a swing, so nothing ever snaps.
    /// </summary>
    public float Stroke { get; init; }

    /// <summary>The right arm's sweep about the vertical, in radians, added to its yaw: negative is out to
    /// the player's right, positive across to the left.</summary>
    public float ArmYaw { get; init; }

    /// <summary>The shoulder flexion (radians forward from hanging) the arm is eased to by <see cref="Stroke"/>.</summary>
    public float Shoulder { get; init; }

    /// <summary>The elbow bend (radians) the arm is eased to by <see cref="Stroke"/>.</summary>
    public float Elbow { get; init; }

    /// <summary>A vertical offset of the hand, in blocks, reached by re-solving the shoulder and elbow so
    /// the shoulder never leaves the torso; 0 leaves the arm as posed.</summary>
    public float HandLift { get; init; }

    /// <summary>The whole body's turn about the feet, in radians (the legs counter-turn so the feet stay planted).</summary>
    public float RootYaw { get; init; }

    /// <summary>The shoulders' twist beyond the body's turn, in radians.</summary>
    public float TorsoTwist { get; init; }

    /// <summary>The torso's roll toward the swing's side, in radians.</summary>
    public float TorsoRoll { get; init; }

    /// <summary>How far the weight sinks into the blow (knees and hips bend, the torso dips, the body drops): 1 is
    /// the slice's strike, more is deeper.</summary>
    public float Dip { get; init; }

    /// <summary>An extra torso pitch in radians, negative leaning forward over the blow, positive back.</summary>
    public float Lean { get; init; }

    /// <summary>Where the right shoulder is pulled toward, in torso space (x right, y up, z back), in blocks.</summary>
    public Vector3 ShoulderPull { get; init; }

    /// <summary>The blade's roll about its own length while carried by the wrist, in radians (an edge-leading cant).</summary>
    public float BladeRoll { get; init; }

    /// <summary>How far the blade is cocked up from the forearm while carried by the wrist, in radians.</summary>
    public float BladeCant { get; init; }

    /// <summary>The blade's turn about the vertical from the forearm's direction, in radians (the tip leading
    /// ahead of the hand's travel), in the engine's yaw convention.</summary>
    public float BladeYaw { get; init; }

    /// <summary>Degrees the pose adds to the held item's own display rotation this frame, in the item JSON's
    /// <c>[pitch, yaw, roll]</c> order (see <see cref="PlayerModel.HeldItemRotationOffset"/>).</summary>
    public Vector3 DisplayOffset { get; init; }
}
