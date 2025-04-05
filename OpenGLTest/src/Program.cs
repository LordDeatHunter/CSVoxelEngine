using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Linq;
using System.Numerics;

namespace silkgltriangle;

internal static class Program
{
    private static Shader _shader;
    private static uint _texture = 0;
    private static Mesh _chunkMesh = null!;

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
        Mesh? mesh = null;
        Camera? camera = null;
        IKeyboard? keyboard = null;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            inputContext = window.CreateInput();
            keyboard = inputContext.Keyboards.FirstOrDefault();

            var chunk = new Chunk();
            _chunkMesh = chunk.GenerateMesh(gl);

            var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
            var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
            _texture = TextureLoader.LoadFromFile(gl, "assets/textures/atlas.png");
            _shader = new Shader(gl, vs, fs);
            _shader.Use();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _texture);
            _shader.SetUniform("uTextureAtlas", 0);

            camera = new Camera(window, inputContext);
            mesh = Mesh.CreateCube(gl);
            gl.Viewport(window.FramebufferSize);

            gl.Enable(GLEnum.DepthTest);
            gl.Enable(GLEnum.DebugOutput);
            gl.DepthFunc(GLEnum.Lequal);
        };

        window.FramebufferResize += newSize => { gl.Viewport(newSize); };

        window.Closing += () =>
        {
            mesh?.Dispose();
            _shader.Dispose();
        };

        window.Update += delta =>
        {
            if (camera == null || keyboard == null) return;

            camera.Update((float)delta, keyboard);
            if (keyboard.IsKeyPressed(Key.Escape)) window.Close();
        };

        window.Render += delta =>
        {
            _shader.Use();
            gl.ClearColor(0.3f, 0.6f, 0.9f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (camera == null) return;

            _shader.SetUniform("uTime", (float)window.Time);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _texture);

            var viewProj = camera.GetViewProjection();
            var mvp = Matrix4x4.CreateTranslation(0, 0, 0) * viewProj;
            _shader.SetUniform("uMVP", mvp);
            
            _chunkMesh.Render();
        };

        window.Run();

        gl.Dispose();
        inputContext.Dispose();
    }
}