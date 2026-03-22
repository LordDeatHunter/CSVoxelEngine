using System;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

public class Camera
{
    public Vector3 Position { get; private set; } = new(0, 0, 3);
    public float Yaw { get; private set; } = -90f;
    public float Pitch { get; private set; } = 0f;
    public bool IsGrounded { get; private set; }
    public float VerticalVelocity => _verticalVelocity;

    private const float DefaultFov = 70f;
    private const float NearPlane = 0.01f;
    private const float FarPlane = 1000f;
    private const float BaseSpeed = 8f;
    private const float MouseSensitivity = 0.2f;
    private const float Gravity = 34f;
    private const float JumpSpeed = 11f;
    private const float PlayerHeight = 1.8f;
    private const float EyeHeight = 1.62f;
    private const float PlayerRadius = 0.30f;
    private const float CollisionStepSize = 0.05f;

    private float _aspectRatio = 16f / 10f;
    private float _verticalVelocity;
    private Vector2 _lastMousePosition;
    private bool _firstMouseMovement = true;

    private readonly IWindow window;
    private readonly IMouse? mouse;

    public Camera(IWindow window, IInputContext input)
    {
        this.window = window;
        mouse = input.Mice.FirstOrDefault();

        window.FramebufferResize += size => { _aspectRatio = size.X / (float)size.Y; };

        if (mouse == null) return;
        mouse.Cursor.CursorMode = CursorMode.Disabled;
        mouse.MouseMove += OnMouseMove;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_firstMouseMovement)
        {
            _lastMousePosition = position;
            _firstMouseMovement = false;
            return;
        }

        var delta = position - _lastMousePosition;
        _lastMousePosition  = position;

        Yaw += delta.X * MouseSensitivity;
        Pitch -= delta.Y * MouseSensitivity;

        Pitch = Math.Clamp(Pitch, -89f, 89f);
    }

    public void Update(float deltaTime, IKeyboard? keyboard, ChunkManager chunkManager)
    {
        if (keyboard == null || chunkManager == null)
            return;

        var moveSpeed = keyboard.IsKeyPressed(Key.ShiftLeft) ? BaseSpeed * 10f : BaseSpeed;
        var direction = GetForwardVector();
        var right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        var forward = Vector3.Normalize(new Vector3(direction.X, 0f, direction.Z));

        Vector3 moveInput = Vector3.Zero;
        if (keyboard.IsKeyPressed(Key.W)) moveInput += forward;
        if (keyboard.IsKeyPressed(Key.S)) moveInput -= forward;
        if (keyboard.IsKeyPressed(Key.A)) moveInput -= right;
        if (keyboard.IsKeyPressed(Key.D)) moveInput += right;

        if (moveInput.LengthSquared() > 1f)
            moveInput = Vector3.Normalize(moveInput);

        if (IsGrounded && keyboard.IsKeyPressed(Key.Space))
        {
            _verticalVelocity = JumpSpeed;
            IsGrounded = false;
        }

        _verticalVelocity -= Gravity * deltaTime;

        var frameMovement = moveInput * moveSpeed * deltaTime;
        frameMovement.Y = _verticalVelocity * deltaTime;

        Position = MoveOnAxis(Position, frameMovement.X, 0, chunkManager, out _);
        Position = MoveOnAxis(Position, frameMovement.Z, 2, chunkManager, out _);

        Position = MoveOnAxis(Position, frameMovement.Y, 1, chunkManager, out var collidedY);
        if (collidedY)
        {
            if (frameMovement.Y < 0f)
                IsGrounded = true;

            _verticalVelocity = 0f;
        }
        else
        {
            IsGrounded = false;
        }
    }

    private Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunkManager, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = Math.Clamp(remaining, -CollisionStepSize, CollisionStepSize);
            var candidate = OffsetAxis(position, axis, step);

            if (IsColliding(candidate, chunkManager))
            {
                collided = true;
                break;
            }

            position = candidate;
            remaining -= step;
        }

        return position;
    }

    private static Vector3 OffsetAxis(Vector3 value, int axis, float delta)
    {
        return axis switch
        {
            0 => new Vector3(value.X + delta, value.Y, value.Z),
            1 => new Vector3(value.X, value.Y + delta, value.Z),
            2 => new Vector3(value.X, value.Y, value.Z + delta),
            _ => value
        };
    }

    private bool IsColliding(Vector3 eyePosition, ChunkManager chunkManager)
    {
        var min = new Vector3(
            eyePosition.X - PlayerRadius,
            eyePosition.Y - EyeHeight,
            eyePosition.Z - PlayerRadius);
        var max = new Vector3(
            eyePosition.X + PlayerRadius,
            eyePosition.Y - EyeHeight + PlayerHeight,
            eyePosition.Z + PlayerRadius);

        var minX = (int)MathF.Floor(min.X);
        var minY = (int)MathF.Floor(min.Y);
        var minZ = (int)MathF.Floor(min.Z);
        var maxX = (int)MathF.Floor(max.X);
        var maxY = (int)MathF.Floor(max.Y);
        var maxZ = (int)MathF.Floor(max.Z);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (IsSolidBlock(x, y, z, chunkManager))
                return true;
        }

        return false;
    }

    private static bool IsSolidBlock(int worldX, int worldY, int worldZ, ChunkManager chunkManager)
    {
        var chunkPos = new Vector3(
            MathF.Floor(worldX / (float)Chunk.Size),
            MathF.Floor(worldY / (float)Chunk.Size),
            MathF.Floor(worldZ / (float)Chunk.Size));

        if (!chunkManager.Chunks.TryGetValue(chunkPos, out var chunk))
            return false;

        var localX = ((worldX % Chunk.Size) + Chunk.Size) % Chunk.Size;
        var localY = ((worldY % Chunk.Size) + Chunk.Size) % Chunk.Size;
        var localZ = ((worldZ % Chunk.Size) + Chunk.Size) % Chunk.Size;

        return chunk.GetBlock(localX, localY, localZ, chunkManager) != 0;
    }

    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(Position, Position + GetForwardVector(), Vector3.UnitY);

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * DefaultFov / 180f, _aspectRatio, NearPlane, FarPlane);

    public Matrix4x4 GetViewProjection() => GetViewMatrix() * GetProjectionMatrix();

    private Vector3 GetForwardVector()
    {
        var yawRad = MathF.PI * Yaw / 180f;
        var pitchRad = MathF.PI * Pitch / 180f;

        return Vector3.Normalize(new Vector3(
            MathF.Cos(pitchRad) * MathF.Cos(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Sin(yawRad)
        ));
    }

    public bool Raycast(float maxDistance, ChunkManager chunkManager, out Vector3 hitPosition, out Vector3 hitNormal)
    {
        var direction = GetForwardVector();
        var currentPosition = Position.Floor();
        var step = direction.Sign();
        var tDelta = Vector3.Abs(Vector3.One / direction);
        var dist = Vector3.Distance(Vector3.Zero, direction);
        var lastStepNormal = Vector3.Zero;

        var tMax = new Vector3(
            (step.X > 0 ? currentPosition.X + 1 - Position.X : Position.X - currentPosition.X) * tDelta.X,
            (step.Y > 0 ? currentPosition.Y + 1 - Position.Y : Position.Y - currentPosition.Y) * tDelta.Y,
            (step.Z > 0 ? currentPosition.Z + 1 - Position.Z : Position.Z - currentPosition.Z) * tDelta.Z
        );

        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Zero;

        for (var i = 0; i < maxDistance * dist; i++)
        {
            var chunkPos = new Vector3(
                MathF.Floor(currentPosition.X / Chunk.Size),
                MathF.Floor(currentPosition.Y / Chunk.Size),
                MathF.Floor(currentPosition.Z / Chunk.Size)
            );

            if (chunkManager.Chunks.TryGetValue(chunkPos, out var chunk))
            {
                var localX = (int)((currentPosition.X % Chunk.Size + Chunk.Size) % Chunk.Size);
                var localY = (int)((currentPosition.Y % Chunk.Size + Chunk.Size) % Chunk.Size);
                var localZ = (int)((currentPosition.Z % Chunk.Size + Chunk.Size) % Chunk.Size);

                if (chunk.GetBlock(localX, localY, localZ, chunkManager) != 0)
                {
                    hitPosition = currentPosition;

                    // If we start inside a solid voxel, approximate with opposite view direction.
                    hitNormal = lastStepNormal == Vector3.Zero ? -direction.Sign() : lastStepNormal;

                    return true;
                }
            }

            if (tMax.X < tMax.Y && tMax.X < tMax.Z)
            {
                currentPosition.X += step.X;
                tMax.X += tDelta.X;
                lastStepNormal = new Vector3(-step.X, 0, 0);
            }
            else if (tMax.Y < tMax.Z)
            {
                currentPosition.Y += step.Y;
                tMax.Y += tDelta.Y;
                lastStepNormal = new Vector3(0, -step.Y, 0);
            }
            else
            {
                currentPosition.Z += step.Z;
                tMax.Z += tDelta.Z;
                lastStepNormal = new Vector3(0, 0, -step.Z);
            }
        }

        return false;
    }
}