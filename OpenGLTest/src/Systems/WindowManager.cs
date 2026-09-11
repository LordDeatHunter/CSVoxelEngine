using System;
using Silk.NET.Windowing;
using Silk.NET.Maths;

namespace silkgltriangle;

public class WindowManager : IDisposable
{
    public IWindow Window { get; private set; }

    public WindowManager()
    {
        var (w, h) = GameSettings.Resolutions[
            Math.Clamp(GameSettings.ResolutionIndex, 0, GameSettings.Resolutions.Length - 1)];
        var maxFps = GameSettings.MaxFpsOptions[
            Math.Clamp(GameSettings.MaxFpsIndex, 0, GameSettings.MaxFpsOptions.Length - 1)];
        var windowOptions = WindowOptions.Default with
        {
            Title = "OpenGL Test",
            PreferredDepthBufferBits = 24,
            PreferredStencilBufferBits = 8,
            Size = new Vector2D<int>(w, h),
            // Debug contexts add driver-side validation overhead; switch back to ContextFlags.Debug when debugging GL issues
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 6)),
            VSync = GameSettings.VSync,
            UpdatesPerSecond = maxFps,
            FramesPerSecond = maxFps,
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