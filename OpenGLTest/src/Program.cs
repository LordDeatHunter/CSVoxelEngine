using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;

namespace silkgltriangle;

public static class Program
{
    public static readonly Dictionary<Vector3, Chunk> Chunks = new();
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
        IMouse? mouse = null;
        GL gl = null!;
        Mesh mesh = null!;
        Camera camera = null!;
        IKeyboard keyboard = null!;
        Shader shader = null!;
        uint texture = 0;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            inputContext = window.CreateInput();
            keyboard = inputContext.Keyboards.FirstOrDefault();
            mouse = inputContext.Mice.FirstOrDefault();

            var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
            var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
            texture = TextureLoader.LoadFromFile(gl, "assets/textures/atlas.png");
            shader = new Shader(gl, vs, fs);
            shader.Use();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, texture);
            shader.SetUniform("uTextureAtlas", 0);

            camera = new Camera(window, inputContext);
            mesh = Mesh.CreateCube(gl);
            gl.Viewport(window.FramebufferSize);

            gl.Enable(GLEnum.DepthTest);
            gl.Enable(GLEnum.DebugOutput);
            gl.DepthFunc(GLEnum.Lequal);

            const int chunkAmount = 20;
            var currentChunk = 0;
            const int maxChunks = chunkAmount * chunkAmount * 2;
            for (var cx = 0; cx < chunkAmount; cx++)
            for (var cy = -1; cy <= 0; cy++)
            for (var cz = 0; cz < chunkAmount; cz++)
            {
                Console.WriteLine("Generating chunk: " + ++currentChunk + "/" + maxChunks);
                var chunkPos = new Vector3(cx, cy, cz);
                var chunk = new Chunk(gl, chunkPos);

                chunk.GenerateMesh();
                Chunks[chunkPos] = chunk;
            }
            
            currentChunk = 0;
            foreach (var chunk in Chunks.Values)
            {
                Console.WriteLine("Regenerating chunk mesh: " + ++currentChunk + "/" + maxChunks);
                chunk.GenerateMesh();
            }
        };

        window.FramebufferResize += newSize => { gl.Viewport(newSize); };

        window.Closing += () =>
        {
            mesh?.Dispose();
            shader.Dispose();
        };

        window.Update += delta =>
        {
            if (camera == null || keyboard == null || mouse == null) return;

            camera.Update((float)delta, keyboard);
            if (keyboard.IsKeyPressed(Key.Escape)) window.Close();

            if (mouse.IsButtonPressed(MouseButton.Left))
            {
                if (!camera.Raycast(10f, out var hitPos, out _)) return;
                var chunkPos = new Vector3(
                    MathF.Floor(hitPos.X / Chunk.Size),
                    MathF.Floor(hitPos.Y / Chunk.Size),
                    MathF.Floor(hitPos.Z / Chunk.Size)
                );

                if (Chunks.TryGetValue(chunkPos, out var chunk))
                {
                    chunk.RemoveBlockAt(hitPos);
                }
            }
        };

        window.Render += delta =>
        {
            shader.Use();
            gl.ClearColor(0.3f, 0.6f, 0.9f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (camera == null) return;

            shader.SetUniform("uTime", (float)window.Time);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, texture);

            var viewProj = camera.GetViewProjection();

            foreach (var chunk in Chunks.Values)
            {
                var mvp = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size) * viewProj;
                shader.SetUniform("uMVP", mvp);
                chunk.Render();
            }
        };

        window.Run();

        gl.Dispose();
        inputContext.Dispose();
    }
}
