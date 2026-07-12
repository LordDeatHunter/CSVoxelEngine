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
    private Shader selectionShader;
    private uint textureAtlas;
    private uint crosshairTexture;
    private Mesh crosshairMesh;
    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    private uint faceVao;
    private uint faceVbo;

    public RenderManager(IWindow window)
    {
        gl = window.CreateOpenGL();
        gl.Enable(GLEnum.DepthTest);
        gl.DepthFunc(GLEnum.Lequal);

        LoadResources();
    }

    private void LoadResources()
    {
        var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
        var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
        var uivs = FileUtils.ReadFile("assets/shaders/ui-vertex.glsl");
        var uifs = FileUtils.ReadFile("assets/shaders/ui-fragment.glsl");
        var selectionVs = FileUtils.ReadFile("assets/shaders/selection-vertex.glsl");
        var selectionFs = FileUtils.ReadFile("assets/shaders/selection-fragment.glsl");

        textureAtlas = TextureLoader.LoadFromFile(gl, "assets/textures/atlas.png");
        crosshairTexture = TextureLoader.LoadFromFile(gl, "assets/textures/crosshair.png");

        worldShader = new Shader(gl, vs, fs);
        worldShader.SetUniform("uTextureAtlas", 0);

        uiShader = new Shader(gl, uivs, uifs);
        uiShader.SetUniform("uTex", 0);

        selectionShader = new Shader(gl, selectionVs, selectionFs);

        crosshairMesh = Mesh.CreateUIQuad(gl, 32);
        CreateSelectionGeometry();
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

    // Reused across frames to avoid a per-frame allocation
    private readonly List<(Chunk chunk, float distSq)> _visibleChunks = new(1024);

    public void RenderWorld(Camera camera, IEnumerable<Chunk> chunks, double time)
    {
        if (camera == null) return;

        worldShader.Use();
        worldShader.SetUniform("uTime", (float)time);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, textureAtlas);

        var viewProj = camera.GetViewProjection();
        var frustum = new Frustum(viewProj);
        var camChunkPos = (camera.Position / Chunk.Size).Floor();

        // Sort chunks by distance for better cache coherency and early z-rejection
        _visibleChunks.Clear();

        foreach (var chunk in chunks)
        {
            // Air-only chunks (the majority) have no mesh — skip before the frustum test
            if (!chunk.HasMesh)
                continue;

            var min = chunk.Position * Chunk.Size;
            var max = min + new Vector3(Chunk.Size);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            var distSq = Vector3.DistanceSquared(chunk.Position, camChunkPos);
            _visibleChunks.Add((chunk, distSq));
        }

        // Sort by distance (front to back for better z-buffer performance)
        _visibleChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        foreach (var (chunk, _) in _visibleChunks)
        {
            chunk.lifetime += 1.0 / 60.0; // TODO: Pass delta time eventually
            
            var mvp = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size) * viewProj;
            worldShader.SetUniform("uMVP", mvp);
            worldShader.SetUniform("uAlpha", MathF.Min(1F, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

            chunk.Render();
        }
    }

    public void RenderBlockSelection(Camera camera, Vector3 blockPosition, Vector3 hitNormal)
    {
        selectionShader.Use();

        var viewProj = camera.GetViewProjection();
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        var center = blockPosition + new Vector3(0.5f);
        var outlineModel = Matrix4x4.CreateScale(1.01f) * Matrix4x4.CreateTranslation(center);
        selectionShader.SetUniform("uMVP", outlineModel * viewProj);
        selectionShader.SetUniform("uColor", new Vector3(1.0f, 0.95f, 0.35f));
        selectionShader.SetUniform("uOpacity", 0.95f);

        gl.LineWidth(2f);
        gl.BindVertexArray(outlineVao);
        unsafe { gl.DrawElements(PrimitiveType.Lines, 24, DrawElementsType.UnsignedInt, null); }

        var faceIndex = FaceIndexFromNormal(hitNormal);
        if (faceIndex >= 0)
        {
            var faceOffset = hitNormal * 0.003f;
            var faceModel = Matrix4x4.CreateTranslation(blockPosition + faceOffset);
            selectionShader.SetUniform("uMVP", faceModel * viewProj);
            selectionShader.SetUniform("uColor", new Vector3(1.0f, 1.0f, 0.5f));
            selectionShader.SetUniform("uOpacity", 0.3f);

            gl.BindVertexArray(faceVao);
            gl.DrawArrays(GLEnum.TriangleFan, faceIndex * 4, 4);
        }

        gl.BindVertexArray(0);
        gl.DepthMask(true);
    }

    private void CreateSelectionGeometry()
    {
        Span<float> outlineVertices =
        [
            -0.5f, -0.5f, -0.5f,
            0.5f, -0.5f, -0.5f,
            0.5f, 0.5f, -0.5f,
            -0.5f, 0.5f, -0.5f,
            -0.5f, -0.5f, 0.5f,
            0.5f, -0.5f, 0.5f,
            0.5f, 0.5f, 0.5f,
            -0.5f, 0.5f, 0.5f
        ];

        Span<uint> outlineIndices =
        [
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        ];

        outlineVao = gl.GenVertexArray();
        outlineVbo = gl.GenBuffer();
        outlineEbo = gl.GenBuffer();

        gl.BindVertexArray(outlineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, outlineVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, outlineVertices, BufferUsageARB.StaticDraw);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, outlineEbo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, outlineIndices, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);

        Span<float> faceVertices =
        [
            0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f,
            1f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 1f, 1f, 1f, 1f,
            0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f,
            1f, 0f, 0f, 1f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 0f,
            0f, 1f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f,
            0f, 0f, 1f, 1f, 0f, 1f, 1f, 0f, 0f, 0f, 0f, 0f
        ];

        faceVao = gl.GenVertexArray();
        faceVbo = gl.GenBuffer();
        gl.BindVertexArray(faceVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, faceVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, faceVertices, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        }
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    private static int FaceIndexFromNormal(Vector3 normal)
    {
        if (normal == new Vector3(0, 0, -1)) return 0;
        if (normal == new Vector3(0, 0, 1)) return 1;
        if (normal == new Vector3(-1, 0, 0)) return 2;
        if (normal == new Vector3(1, 0, 0)) return 3;
        if (normal == new Vector3(0, 1, 0)) return 4;
        if (normal == new Vector3(0, -1, 0)) return 5;
        return -1;
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
        selectionShader.Dispose();
        gl.DeleteBuffer(outlineVbo);
        gl.DeleteBuffer(outlineEbo);
        gl.DeleteVertexArray(outlineVao);
        gl.DeleteBuffer(faceVbo);
        gl.DeleteVertexArray(faceVao);
        gl.Dispose();
    }
}
