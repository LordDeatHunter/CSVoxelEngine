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
        Console.WriteLine($"FPS: {(int)(1 / deltaTime)}");

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true)
        {
            world.TryRemoveBlockFromRaycast(10f);
        }

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true)
        {
            world.TryPlaceBlockFromRaycast(10f, 3);
        }

        world?.Update((float)deltaTime, inputManager.Keyboard);

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