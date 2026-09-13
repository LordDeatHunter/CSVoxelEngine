using System;
using System.IO;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace silkgltriangle;

/// <summary>
/// Standalone item-debugger entrypoint: a window showing the player model in its carrying pose
/// with the selected item in the right hand. Left-drag orbits the camera, right-drag moves the
/// model, middle-drag pans, the wheel zooms (or scrolls the item grid), and the UI edits the
/// item's display transform and previews the walk/mine/jump animations.
/// </summary>
public static class ItemDebuggerProgram
{
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static InputManager _input = null!;
    private static DebugSceneRenderer _scene = null!;
    private static ItemDebuggerUi _ui = null!;
    private static readonly PlayerModel Player = new();
    private static readonly ItemDisplayEditor Editor = new();

    private static float _orbitYaw = 30f;
    private static float _orbitPitch = 12f;
    private static float _distance = 4.5f;
    private static Vector3 _pan;
    private static Vector3 _modelOffset;
    private const float BodyYaw = -90f;
    private static Vector2 _lastMouse;
    private static bool _mouseReady;
    private static bool _leftDrag;
    private static bool _rightDrag;
    private static bool _middleDrag;
    private static string? _dragSlider;
    private static string _status = "";

    private static PreviewMode _mode = PreviewMode.Idle;
    private static float _animTime;
    private static double _time;

    // Set to a PNG path to render a few frames, save one screenshot and quit. Used for
    // scripted verification of the tool without a human at the window.
    private const string ScreenshotVariable = "ITEM_DEBUGGER_SHOT";
    private static string? _screenshotPath;
    private static int _frameCount;
    private static bool _closing;

    public static void Main()
    {
        AssetRoot.UseAssetsDirectory();
        _screenshotPath = Environment.GetEnvironmentVariable(ScreenshotVariable)?.Trim();
        Log.WriteLine($"Item debugger assets: {Directory.GetCurrentDirectory()}");

        var options = WindowOptions.Default with
        {
            Title = "Item Debugger",
            Size = new Vector2D<int>(1600, 900),
            PreferredDepthBufferBits = 24,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
                new APIVersion(4, 6)),
            VSync = true,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Run();
    }

    private static void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);

        _scene = new DebugSceneRenderer(_gl);
        _ui = new ItemDebuggerUi();
        Player.Rig = _scene.Rig;
        Player.HoldingItem = true;
        Player.Update(0.016f, 0f);

        if (_ui.Entries.Count > 0)
            Editor.Select(_ui.Entries[0].Id);
        var select = Environment.GetEnvironmentVariable("ITEM_DEBUGGER_SELECT")?.Trim();
        if (!string.IsNullOrEmpty(select))
            TrySelectByName(select);

        _input = new InputManager(_window);
        if (_input.Mouse is { } mouse)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.Scroll += OnScroll;
        }
    }

    private static bool TrySelectByName(string name)
    {
        foreach (var entry in _ui.Entries)
        {
            var matches = entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (ItemRegistry.TryGet(entry.Id) is { } item
                    && item.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            Editor.Select(entry.Id);
            return true;
        }
        return false;
    }

    private static void OnUpdate(double deltaTime)
    {        if (_closing)
        {
            _window.Close();
            return;
        }

        var dt = Math.Clamp(deltaTime, 0.0, 0.05);
        _time += deltaTime;
        var mouse = _input?.Mouse;

        if (mouse != null)
        {
            var position = new Vector2(mouse.Position.X, mouse.Position.Y);
            if (!_mouseReady)
            {
                _lastMouse = position;
                _mouseReady = true;
            }
            var delta = position - _lastMouse;
            _lastMouse = position;

            if (_leftDrag && mouse.IsButtonPressed(MouseButton.Left))
            {
                _orbitYaw -= delta.X * 0.4f;
                _orbitPitch = Math.Clamp(_orbitPitch + delta.Y * 0.4f, -85f, 85f);
            }

            if (_rightDrag && mouse.IsButtonPressed(MouseButton.Right))
                MoveModel(delta);

            if (_middleDrag && mouse.IsButtonPressed(MouseButton.Middle))
                Pan(delta);

            if (_dragSlider is { } label)
            {
                var slider = _ui.Slider(label);
                if (slider != null && mouse.IsButtonPressed(MouseButton.Left))
                    slider.Set(ItemDebuggerUi.ValueAt(slider, position.X));
                else
                    _dragSlider = null;
            }
        }

        UpdateAnimation((float)dt);
    }

    private static void OnRender(double deltaTime)
    {
        var size = _window.Size;
        if (size.X <= 0 || size.Y <= 0) return;

        var view = BuildView();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            60f * MathF.PI / 180f, size.X / (float)size.Y, 0.05f, 200f);
        var eye = BodyEye();

        _scene.Render(view * projection, eye, BodyYaw, 0f, Player, Editor, (float)_time, size);

        _scene.Ui.Begin(size);
        _ui.Draw(_scene.Ui, Editor, _mode, new Vector2(size.X, size.Y), _status);
        _scene.Ui.End();
        _gl.BindVertexArray(0);

        CaptureScreenshot(size);
    }

    private static void CaptureScreenshot(Vector2D<int> size)
    {
        if (_screenshotPath is not { } path || ++_frameCount < 5) return;

        Console.WriteLine($"[item-debugger] capturing {size.X}x{size.Y} to {path}");
        Console.Out.Flush();
        var pixels = VisualCapture.ReadPixels(_gl, size.X, size.Y);
        VisualCapture.QueueSave(pixels, size.X, size.Y, path);
        VisualCapture.WaitForPending();
        Console.WriteLine("[item-debugger] capture done");
        Console.Out.Flush();
        _closing = true;
    }

    private static Vector3 BodyEye() => _modelOffset + new Vector3(0f, Camera.EyeHeight, 0f);

    private static Matrix4x4 BuildView()
    {
        var target = new Vector3(0f, 0.95f, 0f) + _pan;
        var eye = target + OrbitDirection() * _distance;
        return Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
    }

    private static Vector3 OrbitDirection()
    {
        var yaw = _orbitYaw * MathF.PI / 180f;
        var pitch = _orbitPitch * MathF.PI / 180f;
        return new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
    }

    private static void Pan(Vector2 delta)
    {
        var (right, up, scale) = ScreenBasis();
        _pan += right * (-delta.X * scale) + up * (delta.Y * scale);
    }

    private static void MoveModel(Vector2 delta)
    {
        // Opposite world signs to Pan: moving the camera moves the scene the other way, so the
        // model must translate itself along the drag to follow the cursor by the same amount.
        var (right, up, scale) = ScreenBasis();
        _modelOffset += right * (delta.X * scale) - up * (delta.Y * scale);
    }

    // Camera-plane axes plus a distance-scaled pixel step, shared by pan and model move.
    private static (Vector3 Right, Vector3 Up, float Scale) ScreenBasis()
    {
        var forward = -OrbitDirection();
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        return (right, Vector3.Cross(right, forward), _distance * 0.0016f);
    }

    private static void UpdateAnimation(float dt)
    {
        if (dt <= 0f) return;

        _animTime += dt;
        Player.HoldingItem = true;
        var (speed, sprinting, grounded, vertical) = _mode switch
        {
            PreviewMode.Walk => (8f, false, true, 0f),
            PreviewMode.Run => (12f, true, true, 0f),
            PreviewMode.Jump => (5f, false, false, MathF.Sin(_animTime * 2.5f) * 7f),
            _ => (0f, false, true, 0f),
        };
        Player.Update(dt, speed, sprinting, grounded, vertical);
    }

    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        var position = new Vector2(mouse.Position.X, mouse.Position.Y);

        if (button == MouseButton.Left)
        {
            if (_ui.ButtonAt(position) is { } debugButton)
            {
                HandleButton(debugButton);
                return;
            }
            if (_ui.SliderAt(position) is { } slider)
            {
                _dragSlider = slider.Label;
                slider.Set(ItemDebuggerUi.ValueAt(slider, position.X));
                return;
            }
            if (_ui.SlotAt(position) is { } slot)
            {
                Editor.Select(_ui.Entries[slot].Id);
                _status = "";
                return;
            }
            _leftDrag = true;
            return;
        }

        if (button == MouseButton.Right) _rightDrag = true;
        else if (button == MouseButton.Middle) _middleDrag = true;
    }

    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _leftDrag = false;
            _dragSlider = null;
        }
        else if (button == MouseButton.Right) _rightDrag = false;
        else if (button == MouseButton.Middle) _middleDrag = false;
    }

    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_ui.OverGrid(new Vector2(mouse.Position.X, mouse.Position.Y)))
            _ui.ScrollBy(wheel.Y > 0 ? -1 : 1);
        else
            _distance = Math.Clamp(_distance - wheel.Y * 0.4f, 1.2f, 20f);
    }

    private static void HandleButton(DebugButton button)
    {
        switch (button)
        {
            case DebugButton.Idle:
                _mode = PreviewMode.Idle;
                break;
            case DebugButton.Walk:
                _mode = PreviewMode.Walk;
                break;
            case DebugButton.Run:
                _mode = PreviewMode.Run;
                break;
            case DebugButton.Jump:
                _mode = PreviewMode.Jump;
                break;
            case DebugButton.Mine:
                Player.Punch();
                break;
            case DebugButton.ReloadDisplay:
                Editor.ReloadDisplay();
                _status = "Reloaded from JSON";
                break;
            case DebugButton.ResetRenderer:
                Editor.ResetRenderer();
                _status = "Preview reset";
                break;
            case DebugButton.Save:
                SaveDisplay();
                break;
        }
    }

    private static void SaveDisplay()
    {
        if (Editor.SourcePath is not { } path)
        {
            _status = "Block transforms are not saved";
            return;
        }

        try
        {
            ItemJsonDisplayWriter.Save(path, Editor.Rotation, Editor.Translation, Editor.Scale);
            _status = $"Saved {path}";
            Log.WriteLine(_status);
        }
        catch (Exception e)
        {
            _status = "Save failed: " + e.Message;
            Log.Warn(_status);
        }
    }

    private static void OnClosing()
    {
        _scene?.Dispose();
        _input?.Dispose();
        Log.Shutdown();
    }
}
