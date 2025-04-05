using System;
using System.Collections.Generic;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Linq;
using System.Numerics;

namespace silkgltriangle;

internal static class Program
{
    private static Shader _shader;
    private static readonly List<Vector3> CubePositions = [];
    private static readonly Random Rng = new();

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

        for (var i = 0; i < 10_000; i++)
        {
            CubePositions.Add(
                new Vector3(
                    Rng.Next(-30, 30),
                    Rng.Next(-30, 30),
                    Rng.Next(-30, 30)
                )
            );
        }

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            inputContext = window.CreateInput();
            keyboard = inputContext.Keyboards.FirstOrDefault();

            var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
            var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
            _shader = new Shader(gl, vs, fs);
            _shader.Use();

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

            foreach (var pos in CubePositions)
            {
                var scale = 0.5f + MathF.Abs(pos.X % 2f);
                var model = Matrix4x4.CreateTranslation(pos) *
                            Matrix4x4.CreateRotationX(pos.X) *
                            Matrix4x4.CreateRotationY(pos.Y) *
                            Matrix4x4.CreateRotationZ(pos.Z) *
                            Matrix4x4.CreateScale(scale);
                var mvp = model * camera.GetViewProjection();
                _shader.SetUniform("uMVP", mvp);
                mesh?.Render();
            }
        };

        window.Run();

        gl.Dispose();
        inputContext.Dispose();
    }
}