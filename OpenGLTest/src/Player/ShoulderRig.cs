using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Player;

/// <summary>
/// The camera's F5 view rig: the mode cycle (first person, shoulder, orbit), the pulled-in shoulder
/// offset and the view direction tilted onto the aim point, the orbit body facing, and the crosshair
/// raycast fired along that view.
/// </summary>
internal sealed class ShoulderRig
{
    private readonly Camera _owner;

    internal ShoulderRig(Camera owner) => _owner = owner;

    // Shoulder-surf framing, in blocks, relative to the head eye. RPG-style:
    // behind, above and out to the right shoulder. The view direction stays parallel
    // to the look so aiming (which still fires from the head) keeps its feel.
    private const float ShoulderBackDist = 3.5f;
    private const float ShoulderHeightOffset = 0.7f;
    private const float ShoulderSideOffset = 0.9f;
    // How far short of a blocking wall the pulled-in camera stops, and the march step.
    private const float ShoulderPullMargin = 0.3f;
    private const float ShoulderProbeStep = 0.175f;
    // The camera converges on this point ahead of the head, so the crosshair sits on
    // the same block the raycast hits. Matches World.InteractionReach.
    private const float ShoulderAimDistance = 10f;
    // Lateral glide rate for the shoulder<->orbit side offset, same ballpark as the F5 glide.
    private const float OrbitSideRate = 8f;

    private bool _f5WasPressed;
    private Vector3 _shoulderOffset = Vector3.Zero;
    private Vector3 _shoulderViewDir = Vector3.Zero;
    // Linear 0..1 timer behind ShoulderBlend; toggling mid-glide reverses smoothly.
    // Orbit body facing (degrees) plus its smoothed lateral rig offset, both glided.
    private float _shoulderSide = ShoulderSideOffset;

    internal bool IsShoulderSurf => Mode != CameraMode.FirstPerson;
    internal CameraMode Mode { get; private set; } = CameraMode.FirstPerson;
    internal bool IsOrbit => Mode == CameraMode.Orbit;
    internal Vector3 RenderEye => _owner.Position + _shoulderOffset;
    internal float ShoulderBlend => MathUtils.SmoothStep(ShoulderBlendT);
    internal Vector3 ViewDirection => _shoulderViewDir.NormalizeSafeOr(_owner.GetForwardVector(), 1e-8f);
    internal Vector3 LastAimDirection { get; private set; } = -Vector3.UnitZ;
    internal float ShoulderBlendT { get; set; }
    internal float OrbitPlayerYaw { get; set; }
    internal bool HasActionTarget { get; private set; }
    internal float ActionYaw { get; private set; }
    internal void ClearActionTarget() => HasActionTarget = false;
    internal Vector3 ShoulderOffset { get => _shoulderOffset; set => _shoulderOffset = value; }
    internal Vector3 ShoulderViewDir { get => _shoulderViewDir; set => _shoulderViewDir = value; }
    internal void MarkToggleHeld() => _f5WasPressed = true;

    internal void ResetForTeleport(float yaw)
    {
        _shoulderOffset = Vector3.Zero;
        _shoulderViewDir = Vector3.Zero;
        ShoulderBlendT = IsShoulderSurf ? 1f : 0f;
        OrbitPlayerYaw = yaw;
        _shoulderSide = Mode == CameraMode.Shoulder ? ShoulderSideOffset : 0f;
        HasActionTarget = false;
    }

    internal void NotifyPlayerAction()
    {
        if (!IsOrbit) return;
        ActionYaw = _owner.Yaw;
        HasActionTarget = true;
    }

    internal void UpdateShoulderToggle(IKeyboard keyboard)
    {
        var pressed = keyboard.IsKeyPressed(Key.F5);
        if (pressed && !_f5WasPressed)
        {
            Mode = (CameraMode)(((int)Mode + 1) % 3);
            if (Mode == CameraMode.Orbit)
            {
                OrbitPlayerYaw = _owner.Yaw;
                HasActionTarget = false;
            }
            Log.WriteLine($"Camera {Mode}");
        }

        _f5WasPressed = pressed;
    }

    internal void UpdateShoulderOffset(ChunkManager chunkManager, float deltaTime)
    {
        var forward = _owner.GetForwardVector();
        if (!IsShoulderSurf && ShoulderBlendT <= 0f)
        {
            _shoulderOffset = Vector3.Zero;
            _shoulderViewDir = forward;
            _shoulderSide = Mode == CameraMode.Shoulder ? ShoulderSideOffset : 0f;
            return;
        }

        _shoulderSide = MathUtils.Damp(_shoulderSide,
            Mode == CameraMode.Shoulder ? ShoulderSideOffset : 0f, OrbitSideRate, deltaTime);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var desired = -forward * ShoulderBackDist + Vector3.UnitY * ShoulderHeightOffset + right * _shoulderSide;
        var length = desired.Length();
        if (length < float.Epsilon)
        {
            _shoulderOffset = Vector3.Zero;
            _shoulderViewDir = forward;
            return;
        }

        var steps = Math.Max(1, (int)MathF.Ceiling(length / ShoulderProbeStep));
        var resolved = desired;
        for (var i = 1; i <= steps; i++)
        {
            var probe = _owner.Position + desired * (i / (float)steps);
            if (!IsCameraBlocked(probe, chunkManager))
                continue;

            var safeT = Math.Max(0f, (i - 1) / (float)steps - ShoulderPullMargin / length);
            resolved = desired * safeT;
            break;
        }

        // The glide rides the blend: at 0 the camera sits in the head no matter what the
        // march found, so toggling out slides back along the collision-free line.
        var effective = resolved * ShoulderBlend;
        _shoulderOffset = effective;
        var viewDir = forward * ShoulderAimDistance - effective;
        _shoulderViewDir = viewDir.NormalizeSafeOr(forward, 1e-8f);
    }

    private static bool IsCameraBlocked(Vector3 point, ChunkManager chunkManager)
    {
        var (pointX, pointY, pointZ) = point.BlockCell();
        var id = chunkManager.GetBlockGlobal(pointX, pointY, pointZ);
        return BlockType.IsSolid(id) && BlockType.MovementSpeedOf(id) <= 0f;
    }

    internal void TestSetMode(CameraMode mode)
    {
        Mode = mode;
        ShoulderBlendT = IsShoulderSurf ? 1f : 0f;
        if (mode != CameraMode.Orbit) return;
        OrbitPlayerYaw = _owner.Yaw;
        HasActionTarget = false;
    }

    internal bool Raycast(float maxDistance, ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal,
        out int hitBoxIndex, bool includeFluids)
    {
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;
        hitBoxIndex = 0;

        if (!IsShoulderSurf)
        {
            LastAimDirection = _owner.GetForwardVector();
            return BlockRaycaster.Raycast(_owner.Position, LastAimDirection, maxDistance, 0f, stopAtRange: false,
                chunkManager, out hitPosition, out hitNormal, out hitBoxIndex, out _, includeFluids);
        }

        // Crosshair target from the shoulder camera. Its walk is longer than head reach
        // because it starts behind the head; the head check below cuts it back down.
        LastAimDirection = ViewDirection;
        var camRange = maxDistance + _shoulderOffset.Length();
        if (!BlockRaycaster.Raycast(RenderEye, ViewDirection, camRange, camRange * 3 + 2, stopAtRange: true,
                chunkManager, out var camHit, out var camNormal, out var camBox, out var camT, includeFluids))
            return false;

        // Head validation: the head re-casts toward the exact surface point the camera
        // hit, under first-person rules, and must arrive at the same cell. Range and
        // line-of-sight therefore never exceed first person: nothing is mined or placed
        // through walls or past what the head could hit itself.
        var surface = RenderEye + ViewDirection * camT;
        var toSurface = surface - _owner.Position;
        var headDist = toSurface.Length();
        if (headDist < 1e-4f)
        {
            (hitPosition, hitNormal, hitBoxIndex) = (camHit, camNormal, camBox);
            return true;
        }
        LastAimDirection = toSurface / headDist;
        if (!BlockRaycaster.Raycast(_owner.Position, LastAimDirection, maxDistance, 0f, stopAtRange: false,
                chunkManager, out var headHit, out _, out _, out _, includeFluids))
            return false;
        if (headHit != camHit)
            return false;

        (hitPosition, hitNormal, hitBoxIndex) = (camHit, camNormal, camBox);
        return true;
    }
}
