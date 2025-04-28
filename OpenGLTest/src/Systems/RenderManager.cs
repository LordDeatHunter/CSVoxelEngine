using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
using Silk.NET.Maths;

namespace silkgltriangle;

public class RenderManager : IDisposable
{
    public readonly GL gl;
    private Shader worldShader;
    private Shader uiShader;
    private uint textureAtlas;
    private uint crosshairTexture;
    private Mesh crosshairMesh;

    public RenderManager(IWindow window)
    {
        gl = window.CreateOpenGL();
        gl.Enable(GLEnum.DepthTest);
        gl.Enable(GLEnum.DebugOutput);
        gl.DepthFunc(GLEnum.Lequal);

        LoadResources();
    }

    private void LoadResources()
    {
        var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
        var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
        var uivs = FileUtils.ReadFile("assets/shaders/ui-vertex.glsl");
        var uifs = FileUtils.ReadFile("assets/shaders/ui-fragment.glsl");

        textureAtlas = TextureLoader.LoadFromFile(gl, "assets/textures/atlas.png");
        crosshairTexture = TextureLoader.LoadFromFile(gl, "assets/textures/crosshair.png");

        worldShader = new Shader(gl, vs, fs);
        worldShader.SetUniform("uTextureAtlas", 0);

        uiShader = new Shader(gl, uivs, uifs);
        uiShader.SetUniform("uTex", 0);

        crosshairMesh = Mesh.CreateUIQuad(gl, 32);
    }

    public void SetViewport(Vector2D<int> size)
    {
        gl.Viewport(size);
    }

    public void BeginFrame()
    {
        worldShader.Use();
        gl.ClearColor(0.3f, 0.6f, 0.9f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.Blend);
    }

    public void RenderWorld(Camera camera, IEnumerable<Chunk> chunks, double time)
    {
        if (camera == null) return;

        worldShader.Use();
        worldShader.SetUniform("uTime", (float)time);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, textureAtlas);

        var viewProj = camera.GetViewProjection();
        var frustum = new Frustum(viewProj);

        foreach (var chunk in chunks)
        {
            chunk.lifetime += 1.0 / 60.0; // TODO: Pass delta time eventually
            var min = chunk.Position * Chunk.Size;
            var max = min + new Vector3(Chunk.Size);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            var mvp = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size) * viewProj;
            worldShader.SetUniform("uMVP", mvp);
            worldShader.SetUniform("uAlpha", MathF.Min(1F, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

            chunk.Render();
        }
    }

    public void RenderUI(IWindow window)
    {
        gl.Disable(EnableCap.DepthTest);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        uiShader.Use();
        gl.BindTexture(TextureTarget.Texture2D, crosshairTexture);

        float w = window.Size.X;
        float h = window.Size.Y;
        var projection = Matrix4x4.CreateOrthographicOffCenter(0, w, h, 0, -1f, 1f);
        var center = new Vector3(w / 2f, h / 2f, 0f);
        var model = Matrix4x4.CreateTranslation(center);

        uiShader.SetUniform("uProjection", projection);
        uiShader.SetUniform("uModel", model);

        crosshairMesh.Render();

        gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        crosshairMesh.Dispose();
        worldShader.Dispose();
        uiShader.Dispose();
        gl.Dispose();
    }
}
