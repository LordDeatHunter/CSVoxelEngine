using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Linq;

namespace silkgltriangle;

internal static class Program
{
    public static void Main()
    {
        var windowOptions = WindowOptions.Default with
        {
            Title = "OpenGL Test",
            PreferredDepthBufferBits = 24,
            PreferredStencilBufferBits = 8,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Debug, new APIVersion(3, 3)),
        };

        using var window = Window.Create(windowOptions);
        IInputContext inputContext = null!;
        GL gl = null!;
        QuadRenderer? renderer = null;
        Camera? camera = null;
        IKeyboard? keyboard = null;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            inputContext = window.CreateInput();
            keyboard = inputContext.Keyboards.FirstOrDefault();

            camera = new Camera(window, inputContext);
            renderer = new QuadRenderer(gl);
            gl.Viewport(window.FramebufferSize);

            gl.Enable(GLEnum.DepthTest);
            gl.Enable(GLEnum.DebugOutput);
        };

        window.FramebufferResize += newSize => { gl.Viewport(newSize); };

        window.Closing += () => renderer?.Dispose();

        window.Update += delta =>
        {
            if (camera == null || keyboard == null) return;

            camera.Update((float)delta, keyboard);
            if (keyboard.IsKeyPressed(Key.Escape)) window.Close();
        };

        window.Render += delta =>
        {
            gl.ClearColor(1f, 1f, 1f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (camera == null) return;
            var mvp = camera.GetViewProjection();
            renderer?.Render(mvp);
        };

        window.Run();

        gl.Dispose();
        inputContext.Dispose();
    }
}