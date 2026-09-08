using System;
using Silk.NET.Windowing;
using Silk.NET.Maths;

namespace silkgltriangle;

public class WindowManager : IDisposable
{
    public IWindow Window { get; private set; }

    public WindowManager()
    {
        var windowOptions = WindowOptions.Default with
        {
            Title = "OpenGL Test",
            PreferredDepthBufferBits = 24,
            PreferredStencilBufferBits = 8,
            Size = new Vector2D<int>(1920, 1080),
            // Debug contexts add driver-side validation overhead; switch back to ContextFlags.Debug when debugging GL issues
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 6)),
            VSync = false,
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
        };

        Window = Silk.NET.Windowing.Window.Create(windowOptions);
    }

    public void Run()
    {
        Window.Run();
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}