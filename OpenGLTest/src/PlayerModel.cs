using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Blocky Minecraft-like player model with two-segment limbs: upper arm + forearm and
/// thigh + shin instead of Minecraft's single-piece limbs. Classic Steve proportions at
/// 1.8 blocks tall. Pure pose math (headless-testable) plus cuboid mesh builders.
/// The model faces -Z at yaw -90, i.e. the same direction the camera looks at spawn.
/// </summary>
public class PlayerModel
{
    // Classic Steve layout, in blocks. Legs 0.75 + torso 0.625 + head 0.425 = 1.8.
    public const float TotalHeight = 1.8f;
    public const float HipY = 0.75f;
    public const float TorsoWidth = 0.5f;
    public const float TorsoHeight = 0.625f;
    public const float TorsoDepth = 0.25f;
    public const float NeckY = 1.375f;
    public const float HeadSize = 0.425f;
    // One width for all four limb segments (upper arm, forearm, thigh, shin), so the
    // limbs can never drift to different thicknesses. Slimmer than Steve's 0.25.
    public const float LimbWidth = 0.2f;
    public const float UpperArmLength = 0.375f;
    public const float ForearmLength = 0.375f;
    public const float ThighLength = 0.375f;
    public const float ShinLength = 0.375f;
    public const float ShoulderY = 1.325f;
    // Derived from the widths above: arms hang flush with the torso sides, legs flush
    // with each other under the torso.
    public const float ShoulderX = TorsoWidth / 2f + LimbWidth / 2f;
    public const float HipX = LimbWidth / 2f;

    // Palette swatches (see PalettePixels): solid Blockbench-style colours behind one UV map.
    public const int Skin = 0;
    public const int Shirt = 1;
    public const int Pants = 2;
    public const int Hair = 3;
    public const int Shoe = 4;
    public const int PaletteCols = 4;
    public const int PaletteRows = 2;

    // Gait tuning. WalkSpeedRef matches the camera's base move speed, so Blend hits 1
    // exactly when walking normally and stays capped while sprinting or flying.
    private const float WalkSpeedRef = 8f;
    private const float SwingAmp = 0.65f;
    private const float MoveBlendRate = 8f;
    public const float MineDuration = 0.25f;

    // First-person framing: straight-hanging arms sit ~66 degrees below the view centre,
    // invisible until you pitch well down, and hide behind the chest when you do. Resting
    // them slightly forward with bent elbows and a touch of inward yaw parks the hands in
    // front of the chest, visible at the bottom of the view. Positive X rotation is
    // forward (the model faces -Z, so +pitch moves a hanging hand toward -Z).
    public const float RestShoulderForward = 0.25f;
    public const float RestElbow = 0.30f;
    public const float ArmInwardYaw = 0.12f;

    private float _walkPhase;
    private float _moveBlend;
    private float _mineT = 1f;
    private float _time;

    /// <summary>0 when a mine hit just landed, 1 when the hand is back at rest.</summary>
    public float MineT => _mineT;

    /// <summary>Smoothed 0..1 movement amount driving the swing amplitude.</summary>
    public float MoveBlend => _moveBlend;

    /// <summary>
    /// Advances the walk cycle and the mine swing. <paramref name="horizontalSpeed"/> is in
    /// blocks/s (the camera's XZ displacement per second); standing still eases the limbs
    /// back to rest instead of snapping.
    /// </summary>
    public void Update(float deltaTime, float horizontalSpeed)
    {
        if (deltaTime <= 0f) return;
        _time += deltaTime;

        var target = MathUtils.Clamp01(horizontalSpeed / WalkSpeedRef);
        _moveBlend = MathUtils.Damp(_moveBlend, target, MoveBlendRate, deltaTime);

        // Stride rate grows with speed so feet roughly keep up with the ground.
        _walkPhase += deltaTime * (4f + horizontalSpeed * 0.9f);

        if (_mineT < 1f)
            _mineT = Math.Min(1f, _mineT + deltaTime / MineDuration);
    }

    /// <summary>Starts one hand swing (a mining hit just landed).</summary>
    public void Punch() => _mineT = 0f;

    /// <summary>Joint angles in radians plus the vertical bob, for this frame.</summary>
    public readonly struct Pose
    {
        public readonly float LeftHip;
        public readonly float RightHip;
        public readonly float LeftKnee;
        public readonly float RightKnee;
        public readonly float LeftShoulder;
        public readonly float RightShoulder;
        public readonly float LeftElbow;
        public readonly float RightElbow;
        public readonly float TorsoPitch;
        public readonly float Bob;

        public Pose(float leftHip, float rightHip, float leftKnee, float rightKnee,
            float leftShoulder, float rightShoulder, float leftElbow, float rightElbow,
            float torsoPitch, float bob)
        {
            LeftHip = leftHip;
            RightHip = rightHip;
            LeftKnee = leftKnee;
            RightKnee = rightKnee;
            LeftShoulder = leftShoulder;
            RightShoulder = rightShoulder;
            LeftElbow = leftElbow;
            RightElbow = rightElbow;
            TorsoPitch = torsoPitch;
            Bob = bob;
        }
    }

    public Pose GetPose()
    {
        var swing = MathF.Sin(_walkPhase) * SwingAmp * _moveBlend;

        // Hips and shoulders swing opposite; knees flex (backward, hence negative) with a
        // phase lag so the joint reads instead of the leg swinging like a plank. Elbows
        // stay slightly bent at rest and flex forward as the arm swings.
        var leftKnee = -(Math.Max(0f, MathF.Sin(_walkPhase - 0.7f)) * 0.9f * _moveBlend + 0.03f);
        var rightKnee = -(Math.Max(0f, MathF.Sin(_walkPhase + MathF.PI - 0.7f)) * 0.9f * _moveBlend + 0.03f);
        var leftShoulder = RestShoulderForward - swing + MathF.Sin(_time * 1.7f) * 0.02f;
        var rightShoulder = RestShoulderForward + swing - MathF.Sin(_time * 1.7f) * 0.02f;
        var leftElbow = (Math.Max(0f, MathF.Sin(_walkPhase)) * 0.45f * _moveBlend) + RestElbow;
        var rightElbow = (Math.Max(0f, -MathF.Sin(_walkPhase)) * 0.45f * _moveBlend) + RestElbow;

        // Mining raises the right hand up-forward into view, peaking mid-swing, on top of
        // whatever the walk cycle is doing. sin(pi*t) is 0 at both ends, 1 in the middle.
        if (_mineT < 1f)
        {
            var pump = MathF.Sin(MathF.PI * _mineT);
            rightShoulder += pump * 1.3f;
            rightElbow += pump * 0.5f;
        }

        var bob = MathF.Abs(MathF.Cos(_walkPhase)) * 0.04f * _moveBlend;
        var torsoPitch = -_moveBlend * 0.06f - (_mineT < 1f ? MathF.Sin(MathF.PI * _mineT) * 0.03f : 0f);
        return new Pose(swing, -swing, leftKnee, rightKnee,
            leftShoulder, rightShoulder, leftElbow, rightElbow, torsoPitch, bob);
    }

    /// <summary>
    /// World matrices for every part, keyed by mesh name (see <see cref="MeshNames"/>).
    /// Thigh/Shin/UpperArm/Forearm entries are one side's; the caller draws each twice
    /// with the Left/Right matrices. <paramref name="eyePos"/> is the camera eye (feet are
    /// <see cref="Camera.EyeHeight"/> below it), <paramref name="yawDeg"/>/<paramref name="pitchDeg"/>
    /// the camera look. <paramref name="backOffset"/> slides the whole body away from the
    /// facing direction (first-person framing; the shadow pass leaves it at 0).
    /// </summary>
    public Matrix4x4 GetPartMatrix(string part, Vector3 eyePos, float yawDeg, float pitchDeg, float backOffset = 0f)
    {
        var pose = GetPose();
        var feet = eyePos - new Vector3(0f, Camera.EyeHeight, 0f);
        if (backOffset != 0f)
        {
            var yawBase = yawDeg * MathF.PI / 180f;
            feet -= new Vector3(MathF.Cos(yawBase) * backOffset, 0f, MathF.Sin(yawBase) * backOffset);
        }
        // Model faces -Z at rest; this yaw maps it onto the camera's horizontal facing
        // (at the default -90 it is the identity, verified against Camera.GetForwardVector).
        var yawRad = -(yawDeg + 90f) * MathF.PI / 180f;
        var root = Matrix4x4.CreateRotationY(yawRad) * Matrix4x4.CreateTranslation(feet + new Vector3(0f, pose.Bob, 0f));
        var swingAxis = Vector3.UnitX;

        Matrix4x4 Torso() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pose.TorsoPitch)
            * Matrix4x4.CreateTranslation(0f, HipY, 0f) * root;

        Matrix4x4 Head() =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, pitchDeg * MathF.PI / 180f * 0.8f)
            * Matrix4x4.CreateTranslation(0f, NeckY, 0f) * root;

        Matrix4x4 UpperArm(float x, float shoulder)
        {
            // Drift the hands toward the view centre so they read at the bottom of the
            // frame instead of out at the edges. Right arm (+X) yaws one way, left the other.
            var inward = (x > 0f ? 1f : -1f) * ArmInwardYaw;
            return Matrix4x4.CreateFromAxisAngle(swingAxis, shoulder)
                * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, inward)
                * Matrix4x4.CreateTranslation(x, ShoulderY, 0f) * root;
        }

        Matrix4x4 Thigh(float x, float hip) =>
            Matrix4x4.CreateFromAxisAngle(swingAxis, hip)
            * Matrix4x4.CreateTranslation(x, HipY, 0f) * root;

        return part switch
        {
            "Torso" => Torso(),
            "Head" => Head(),
            "UpperArmL" => UpperArm(-ShoulderX, pose.LeftShoulder),
            "UpperArmR" => UpperArm(ShoulderX, pose.RightShoulder),
            "ForearmL" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.LeftElbow)
                * Matrix4x4.CreateTranslation(0f, -UpperArmLength, 0f)
                * UpperArm(-ShoulderX, pose.LeftShoulder),
            "ForearmR" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.RightElbow)
                * Matrix4x4.CreateTranslation(0f, -UpperArmLength, 0f)
                * UpperArm(ShoulderX, pose.RightShoulder),
            "ThighL" => Thigh(-HipX, pose.LeftHip),
            "ThighR" => Thigh(HipX, pose.RightHip),
            "ShinL" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.LeftKnee)
                * Matrix4x4.CreateTranslation(0f, -ThighLength, 0f)
                * Thigh(-HipX, pose.LeftHip),
            "ShinR" => Matrix4x4.CreateFromAxisAngle(swingAxis, pose.RightKnee)
                * Matrix4x4.CreateTranslation(0f, -ThighLength, 0f)
                * Thigh(HipX, pose.RightHip),
            _ => Matrix4x4.Identity,
        };
    }

    /// <summary>Mesh name per draw; arms/legs draw one mesh twice (left + right matrices).</summary>
    public static readonly string[] BodyParts =
        ["Torso", "UpperArmL", "UpperArmR", "ForearmL", "ForearmR", "ThighL", "ThighR", "ShinL", "ShinR"];

    // First-person draws everything but the head. The torso only reads when the eye sits
    // forward of the chest plane (like real eyes forward of the sternum), so the whole
    // body shifts back a touch in the main pass: looking down then shows the shirt front
    // cascading into legs and feet instead of the neck. The shift must clear half the
    // torso depth (0.125) or the straight-down ray still lands on the collar. Shadows
    // stay rooted at the true feet so they never detach from the ground.
    public const float FirstPersonBodyBack = 0.2f;

    public static readonly string[] FirstPersonParts =
        ["Torso", "UpperArmL", "UpperArmR", "ForearmL", "ForearmR", "ThighL", "ThighR", "ShinL", "ShinR"];

    /// <summary>Which shared mesh each body part draws (left/right share one).</summary>
    public static string MeshFor(string part) => part switch
    {
        "UpperArmL" or "UpperArmR" => "UpperArm",
        "ForearmL" or "ForearmR" => "Forearm",
        "ThighL" or "ThighR" => "Thigh",
        "ShinL" or "ShinR" => "Shin",
        _ => part,
    };

    /// <summary>
    /// The 4x2 solid-colour palette texture (top-first RGBA): skin, shirt, pants, hair,
    /// shoe, then white spares. Every cuboid face UVs to one swatch centre, so the model
    /// is UV-mapped Blockbench-style with zero image assets to author.
    /// </summary>
    public static byte[] PalettePixels()
    {
        // Row 0 (top): skin, shirt, pants, hair. Row 1: shoe, white, white, white.
        (byte r, byte g, byte b)[] swatches =
        [
            (201, 149, 108), // skin
            (0, 170, 170),   // shirt (Steve cyan)
            (58, 58, 210),   // pants (Steve blue)
            (74, 47, 24),    // hair
            (90, 90, 90),    // shoe
            (255, 255, 255),
            (255, 255, 255),
            (255, 255, 255),
        ];
        var pixels = new byte[PaletteCols * PaletteRows * 4];
        for (var i = 0; i < swatches.Length; i++)
        {
            pixels[i * 4 + 0] = swatches[i].r;
            pixels[i * 4 + 1] = swatches[i].g;
            pixels[i * 4 + 2] = swatches[i].b;
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    /// <summary>UV centre of a palette swatch (v = 0 is the top row, like TextureLoader).</summary>
    public static Vector2 SwatchUv(int swatch) =>
        new(((swatch % PaletteCols) + 0.5f) / PaletteCols, ((swatch / PaletteCols) + 0.5f) / PaletteRows);

    // Face order is Chunk.CubeFaces: -Z (front, the face the model looks through), +Z, -X,
    // +X, +Y, -Y. Head wears hair everywhere but the face; the torso's collar and waistband
    // peek skin/pants; forearms are bare skin under shirt sleeves; shins end in shoes.
    private static readonly int[] HeadSwatches = [Skin, Hair, Hair, Hair, Hair, Skin];
    private static readonly int[] TorsoSwatches = [Shirt, Shirt, Shirt, Shirt, Skin, Pants];
    private static readonly int[] UpperArmSwatches = [Shirt, Shirt, Shirt, Shirt, Shirt, Shirt];
    private static readonly int[] ForearmSwatches = [Skin, Skin, Skin, Skin, Shirt, Skin];
    private static readonly int[] ThighSwatches = [Pants, Pants, Pants, Pants, Pants, Pants];
    private static readonly int[] ShinSwatches = [Pants, Pants, Pants, Pants, Pants, Shoe];

    /// <summary>
    /// One mesh per shared shape (6 total): torso (origin at hips, grows up), head (origin
    /// at neck, grows up), upper arm / thigh (origin at shoulder / hip, hang down),
    /// forearm / shin (origin at elbow / knee, hang down). Child pivots compose by
    /// translating down the parent length (see <see cref="GetPartMatrix"/>).
    /// </summary>
    public static Dictionary<string, Mesh> BuildMeshes(GL gl) => new()
    {
        ["Torso"] = CreateCuboid(gl, TorsoWidth, TorsoHeight, TorsoDepth, TorsoHeight * 0.5f, TorsoSwatches),
        ["Head"] = CreateCuboid(gl, HeadSize, HeadSize, HeadSize, HeadSize * 0.5f, HeadSwatches),
        ["UpperArm"] = CreateCuboid(gl, LimbWidth, UpperArmLength, LimbWidth, -UpperArmLength * 0.5f, UpperArmSwatches),
        ["Forearm"] = CreateCuboid(gl, LimbWidth, ForearmLength, LimbWidth, -ForearmLength * 0.5f, ForearmSwatches),
        ["Thigh"] = CreateCuboid(gl, LimbWidth, ThighLength, LimbWidth, -ThighLength * 0.5f, ThighSwatches),
        ["Shin"] = CreateCuboid(gl, LimbWidth, ShinLength, LimbWidth, -ShinLength * 0.5f, ShinSwatches),
    };

    /// <summary>
    /// A cuboid in the shared <see cref="Vertex"/> layout, centred on x/z with its y span
    /// at <paramref name="centerY"/> (pivot-relative, so limbs hang down from their joint).
    /// Each face samples one palette swatch; the vertex colour stays white.
    /// </summary>
    internal static Mesh CreateCuboid(GL gl, float sx, float sy, float sz, float centerY, int[] faceSwatches)
    {
        var vertices = new Vertex[24];
        var indices = new ushort[36];

        for (var face = 0; face < 6; face++)
        {
            var positions = Chunk.CubeFaces[face].face;
            var normal = Chunk.CubeFaces[face].normal;
            var uv = SwatchUv(faceSwatches[face]);

            for (var corner = 0; corner < 4; corner++)
            {
                var p = positions[corner];
                vertices[face * 4 + corner] = new Vertex(
                    new Vector3((p.X - 0.5f) * sx, (p.Y - 0.5f) * sy + centerY, (p.Z - 0.5f) * sz),
                    255, 255, 255, 255, uv, 0, 1f, 0f, normal);
            }

            var o = (ushort)(face * 4);
            indices[face * 6 + 0] = o;
            indices[face * 6 + 1] = (ushort)(o + 1);
            indices[face * 6 + 2] = (ushort)(o + 2);
            indices[face * 6 + 3] = o;
            indices[face * 6 + 4] = (ushort)(o + 2);
            indices[face * 6 + 5] = (ushort)(o + 3);
        }

        return new Mesh(gl, vertices, indices);
    }
}
