using System;
using System.Globalization;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace silkgltriangle;

public static class Program
{
    private static WindowManager windowManager;
    private static InputManager inputManager;
    private static RenderManager renderManager;
    private static World world;
    private static bool debugEnabled;
    private static bool xWasPressed;
    private static bool qWasPressed;
    private static double debugLogTimer;
    // TODO: move this to player class
    private static double placeTimer = MAX_PLACE_TIMER;
    private static readonly double MAX_PLACE_TIMER = 0.2;

    // Set to a number of seconds to jump the day/night cycle at startup
    private const string TimeOffsetVariable = "VOXEL_TIME";

    // Added to the window clock before the lighting pass sees it, so a screenshot can be taken
    // at any point of the day without waiting for the sun to get there
    private static double timeOffset;

    public static void Main()
    {
        windowManager = new WindowManager();

        windowManager.Window.Load += OnLoad;
        windowManager.Window.Update += OnUpdate;
        windowManager.Window.Render += OnRender;
        windowManager.Window.Closing += OnClosing;
        windowManager.Window.FramebufferResize += OnFramebufferResize;

        windowManager.Run();
    }

    private static void OnLoad()
    {
        timeOffset = ReadTimeOffset();

        inputManager = new InputManager(windowManager.Window);
        renderManager = new RenderManager(windowManager.Window);
        world = new World(renderManager.gl, windowManager.Window, inputManager.InputContext);

        inputManager.Mouse?.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Right) placeTimer = MAX_PLACE_TIMER;
        };

        inputManager.Mouse?.Scroll += (_, wheel) => world.Inventory.Scroll(wheel.Y);
    }

    /// <summary>
    /// Dev override: VOXEL_TIME="seconds" offsets the clock the lighting pass reads, so noon,
    /// dusk or night can be photographed straight away instead of after minutes of orbit.
    /// Parsed once here; an unparseable value is ignored with a warning, like VOXEL_SPAWN.
    /// </summary>
    private static double ReadTimeOffset()
    {
        var timeOverride = Environment.GetEnvironmentVariable(TimeOffsetVariable);
        if (string.IsNullOrWhiteSpace(timeOverride))
            return 0.0;

        if (double.TryParse(timeOverride.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var offset))
            return offset;

        Console.WriteLine(
            $"Ignoring {TimeOffsetVariable}=\"{timeOverride}\": expected a number of seconds, e.g. 150");
        return 0.0;
    }


    private static void OnUpdate(double deltaTime)
    {
        placeTimer = Math.Min(MAX_PLACE_TIMER, placeTimer + deltaTime);
        var xPressed = inputManager.Keyboard?.IsKeyPressed(Key.X) == true;
        if (xPressed && !xWasPressed)
        {
            debugEnabled = !debugEnabled;
            Console.WriteLine($"Debug {(debugEnabled ? "ON" : "OFF")}");
        }
        xWasPressed = xPressed;

        // One throw per press, so holding Q doesn't empty the slot; Ctrl+Q throws the stack
        var qPressed = inputManager.Keyboard?.IsKeyPressed(Key.Q) == true;
        if (qPressed && !qWasPressed)
        {
            var wholeStack = inputManager.Keyboard?.IsKeyPressed(Key.ControlLeft) == true
                || inputManager.Keyboard?.IsKeyPressed(Key.ControlRight) == true;
            world.ThrowSelected(wholeStack);
        }
        qWasPressed = qPressed;

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true && placeTimer >= MAX_PLACE_TIMER)
        {
            placeTimer = 0;
            world.TryUseSelected(World.InteractionReach);
        }

        world.Update((float)deltaTime, inputManager.Keyboard);
        world.UpdateBlockBreaking(deltaTime, inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);

        if (debugEnabled)
        {
            debugLogTimer += deltaTime;
            if (debugLogTimer >= 0.2)
            {
                var fps = deltaTime > 0 ? (int)(1 / deltaTime) : 0;
                var camera = world.Camera;
                var position = camera.Position;
                Console.WriteLine(
                    $"FPS {fps} | Pos ({position.X:F2}, {position.Y:F2}, {position.Z:F2}) | Grounded {camera.IsGrounded} | Vy {camera.VerticalVelocity:F2} | Water {camera.IsInWater}/{camera.IsEyeInWater}");
                debugLogTimer = 0;
            }
        }

        if (inputManager.Keyboard?.IsKeyPressed(Key.Escape) == true)
            windowManager.Window.Close();
    }

    private static void OnRender(double deltaTime)
    {
        renderManager.SetLighting(windowManager.Window.Time + timeOffset, world.Camera, world.PrimedTnt,
            world.Camera.IsEyeInWater);
        renderManager.BeginFrame(world.Camera);
        world.Render(renderManager);
        world.RenderUI(renderManager);
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        renderManager.SetViewport(size);
    }

    private static void OnClosing()
    {
        renderManager?.Dispose();
        inputManager?.Dispose();
    }
}