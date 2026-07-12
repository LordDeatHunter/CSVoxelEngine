using System;
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
    private static double debugLogTimer;
    // TODO: move this to player class
    private static double placeTimer = MAX_PLACE_TIMER;
    private static readonly double MAX_PLACE_TIMER = 0.2;

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
        inputManager = new InputManager(windowManager.Window);
        renderManager = new RenderManager(windowManager.Window);
        world = new World(renderManager.gl, windowManager.Window, inputManager.InputContext);

        inputManager.Mouse?.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Right) placeTimer = MAX_PLACE_TIMER;
        };
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

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true && placeTimer >= MAX_PLACE_TIMER)
        {
            placeTimer = 0;
            world.TryPlaceBlockFromRaycast(World.InteractionReach, 3);
        }

        world.Update((float)deltaTime, inputManager.Keyboard);
        world.UpdateBlockBreaking(deltaTime, inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);

        if (debugEnabled)
        {
            debugLogTimer += deltaTime;
            if (debugLogTimer >= 0.2)
            {
                var fps = deltaTime > 0 ? (int)(1 / deltaTime) : 0;
                var position = world.Camera.Position;
                Console.WriteLine(
                    $"FPS {fps} | Pos ({position.X:F2}, {position.Y:F2}, {position.Z:F2}) | Grounded {world.Camera.IsGrounded} | Vy {world.Camera.VerticalVelocity:F2}");
                debugLogTimer = 0;
            }
        }

        if (inputManager.Keyboard?.IsKeyPressed(Key.Escape) == true)
            windowManager.Window.Close();
    }

    private static void OnRender(double deltaTime)
    {
        renderManager.BeginFrame();
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