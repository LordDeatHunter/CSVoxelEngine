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
    }
    
    private static void OnUpdate(double deltaTime)
    {
        var xPressed = inputManager.Keyboard?.IsKeyPressed(Key.X) == true;
        if (xPressed && !xWasPressed)
        {
            debugEnabled = !debugEnabled;
            Console.WriteLine($"Debug {(debugEnabled ? "ON" : "OFF")}");
        }
        xWasPressed = xPressed;

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true)
        {
            world.TryRemoveBlockFromRaycast(10f);
        }

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true)
        {
            world.TryPlaceBlockFromRaycast(10f, 3);
        }

        world?.Update((float)deltaTime, inputManager.Keyboard);

        if (debugEnabled && world != null)
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