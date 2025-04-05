using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace silkgltriangle;

internal static class Program
{
    public static void Main(string[] args)
    {
        var windowOptions = WindowOptions.Default with
        {
            Title = "OpenGL Test",
            PreferredDepthBufferBits = 24,
            PreferredStencilBufferBits = 8,
            API = new GraphicsAPI(ContextAPI.OpenGL, new APIVersion(3, 3))
        };

        using var window = Window.Create(windowOptions);
        IInputContext inputContext = null!;
        GL gl = null!;
        TriangleRenderer? renderer = null;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            inputContext = window.CreateInput();

            renderer = new TriangleRenderer(gl);
            gl.Viewport(window.FramebufferSize);
        };

        window.Render += deltaSeconds =>
        {
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            renderer?.Render();
        };

        window.FramebufferResize += newSize => { gl.Viewport(newSize); };

        window.Closing += () =>
        {
            renderer?.Dispose();
        };

        window.Run();

        gl.Dispose();
        inputContext.Dispose();
    }
}