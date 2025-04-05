using System;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

public class Camera
{
    public Vector3 Position = new(0, 0, 3);
    public float Yaw = -90f;
    public float Pitch = 0f;

    private float _fov = 70f;
    private float _aspectRatio = 1.6f;
    private float _speed = 4f;
    private float _sensitivity = 0.2f;

    private Vector2 _lastMouse;
    private bool _firstMouse = true;

    public Camera(IWindow window, IInputContext input)
    {
        window.FramebufferResize += size => { _aspectRatio = size.X / (float)size.Y; };

        var mouse = input.Mice.FirstOrDefault();
        if (mouse == null) return;
        mouse.Cursor.CursorMode = CursorMode.Disabled;
        mouse.MouseMove += OnMouseMove;
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (_firstMouse)
        {
            _lastMouse = pos;
            _firstMouse = false;
            return;
        }

        var delta = pos - _lastMouse;
        _lastMouse = pos;

        Yaw += delta.X * _sensitivity;
        Pitch -= delta.Y * _sensitivity;

        Pitch = Math.Clamp(Pitch, -89f, 89f);
    }

    public void Update(float deltaTime, IKeyboard? keyboard)
    {
        var direction = GetForward();
        var right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        var forward = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, right));

        if (keyboard == null) return;
        
        var moveSpeed = keyboard.IsKeyPressed(Key.ShiftLeft) ? _speed * 2 : _speed;

        if (keyboard.IsKeyPressed(Key.W)) Position += forward * moveSpeed * deltaTime;
        if (keyboard.IsKeyPressed(Key.S)) Position -= forward * moveSpeed * deltaTime;
        if (keyboard.IsKeyPressed(Key.A)) Position -= right * moveSpeed * deltaTime;
        if (keyboard.IsKeyPressed(Key.D)) Position += right * moveSpeed * deltaTime;
        if (keyboard.IsKeyPressed(Key.Space)) Position += Vector3.UnitY * _speed * deltaTime;
        if (keyboard.IsKeyPressed(Key.ControlLeft)) Position -= Vector3.UnitY * _speed * deltaTime;
    }

    public Matrix4x4 GetViewMatrix() => Matrix4x4.CreateLookAt(Position, Position + GetForward(), Vector3.UnitY);

    public Matrix4x4 GetProjectionMatrix() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * _fov / 180f, _aspectRatio, 0.01f, 100f);

    public Matrix4x4 GetViewProjection() => GetViewMatrix() * GetProjectionMatrix();

    private Vector3 GetForward()
    {
        var yawRad = MathF.PI * Yaw / 180f;
        var pitchRad = MathF.PI * Pitch / 180f;

        return Vector3.Normalize(new Vector3(
            MathF.Cos(pitchRad) * MathF.Cos(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Sin(yawRad)
        ));
    }
}