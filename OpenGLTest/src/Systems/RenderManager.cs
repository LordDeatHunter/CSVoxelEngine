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
    private Shader selectionShader;
    private Shader breakShader;
    private UIRenderer ui;
    private BlockCubeCache blockCubes;
    private uint textureAtlas;
    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    private uint faceVao;
    private uint faceVbo;

    // Directional shadow mapping is parked (see SampleShadow in test-fragment.glsl);
    // when it comes back, the shadow FBO / light-space matrix live here again.
    private Vector2D<int> _viewportSize;

    // Per-frame lighting state, pushed once per frame via SetLighting and applied
    // to the world shader by every world draw through ApplyLightingUniforms
    private Vector3 _sunDir = Vector3.UnitY;
    private Vector3 _sunColor = Vector3.One;
    private float _sunIntensity = 1f;
    private Vector3 _ambient = new(0.42f, 0.45f, 0.52f);
    private readonly Vector3 _torchColor = new(1f, 0.62f, 0.25f);
    private const float TorchIntensity = 1.2f;
    private Vector3 _fogColor = new(0.3f, 0.6f, 0.9f);
    private Vector3 _skyColor = new(0.3f, 0.6f, 0.9f);
    private Vector3 _cameraPos;
    private float _time;
    private readonly Vector3[] _pointPos = new Vector3[WorldGenerationConfig.MaxPointLights];
    private readonly Vector3[] _pointColors = new Vector3[WorldGenerationConfig.MaxPointLights];
    private int _pointCount;

    public RenderManager(IWindow window)
    {
        gl = window.CreateOpenGL();
        gl.Enable(GLEnum.DepthTest);
        gl.DepthFunc(GLEnum.Lequal);
        _viewportSize = window.Size;

        LoadResources();
    }

    private void LoadResources()
    {
        var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
        var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
        var selectionVs = FileUtils.ReadFile("assets/shaders/selection-vertex.glsl");
        var selectionFs = FileUtils.ReadFile("assets/shaders/selection-fragment.glsl");
        var breakVs = FileUtils.ReadFile("assets/shaders/break-vertex.glsl");
        var breakFs = FileUtils.ReadFile("assets/shaders/break-fragment.glsl");

        textureAtlas = TextureAtlas.Build(gl, "assets/textures/blocks");
        BlockType.ResolveAtlasTiles();

        worldShader = new Shader(gl, vs, fs);
        // SetUniform writes to the currently bound program, so bind first — otherwise
        // the sampler unit silently stays at its default.
        worldShader.Use();
        worldShader.SetUniform("uTextureAtlas", 0);

        selectionShader = new Shader(gl, selectionVs, selectionFs);
        breakShader = new Shader(gl, breakVs, breakFs);

        blockCubes = new BlockCubeCache(gl);
        ui = new UIRenderer(gl, textureAtlas, blockCubes);
        CreateSelectionGeometry();
    }

    public void SetViewport(Vector2D<int> size)
    {
        _viewportSize = size;
        gl.Viewport(size);
    }

    /// <summary>
    /// Advances the day/night cycle and pushes the resulting sun, sky and point-light
    /// state into the per-frame fields. Called once per frame before <see cref="BeginFrame"/>;
    /// every world draw applies the state through <see cref="ApplyLightingUniforms"/>.
    /// </summary>
    public void SetLighting(double time, Camera? camera, IReadOnlyList<PrimedTnt>? primed)
    {
        _time = (float)time;
        if (camera != null)
            _cameraPos = camera.Position;

        // The sun orbits once per DayLengthSeconds; the phase starts it mid-morning
        var angle = (float)(time / WorldGenerationConfig.DayLengthSeconds) * MathF.Tau + 0.8f;
        _sunDir = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        var elevation = _sunDir.Y;

        // Parked: the shadow camera (ortho box + light-space matrix) used the elevation
        // to disable itself at night; it will again when shadows come back.
        var dayness = Smooth01((elevation + 0.08f) / 0.33f);
        var duskAmount = elevation > -0.1f ? Math.Clamp(1f - MathF.Abs(elevation) * 5f, 0f, 1f) : 0f;

        _sunColor = Mix(new Vector3(0.3f, 0.4f, 0.7f), new Vector3(1f, 0.96f, 0.88f), dayness);
        _sunColor = Mix(_sunColor, new Vector3(1f, 0.5f, 0.25f), duskAmount * 0.55f);
        _sunIntensity = 0.05f + 1.2f * dayness;

        _ambient = Mix(new Vector3(0.06f, 0.07f, 0.11f), new Vector3(0.42f, 0.45f, 0.52f), dayness);

        _skyColor = Mix(new Vector3(0.012f, 0.018f, 0.045f), new Vector3(0.3f, 0.6f, 0.9f), dayness);
        _skyColor = Mix(_skyColor, new Vector3(0.98f, 0.45f, 0.2f), duskAmount * 0.5f);
        // Fog melts the far terrain (and the LOD edge) into the sky, day and night
        _fogColor = _skyColor;

        // Primed TNT strobes feed the dynamic point lights; lamp glow is baked voxel light
        _pointCount = 0;
        if (primed != null)
        {
            foreach (var tnt in primed)
            {
                if (_pointCount >= WorldGenerationConfig.MaxPointLights) break;
                _pointPos[_pointCount] = tnt.Position;
                var flash = tnt.WhiteFlash;
                _pointColors[_pointCount] = new Vector3(1f, 0.45f, 0.15f) * 1.2f
                    + new Vector3(1f, 0.9f, 0.8f) * (flash * 2f);
                _pointCount++;
            }
        }
        for (var i = _pointCount; i < WorldGenerationConfig.MaxPointLights; i++)
        {
            _pointPos[i] = Vector3.Zero;
            _pointColors[i] = Vector3.Zero;
        }

    }

    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector3 Mix(Vector3 a, Vector3 b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    public void BeginFrame()
    {
        worldShader.Use();
        gl.ClearColor(_skyColor.X, _skyColor.Y, _skyColor.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.Blend);
    }

    /// <summary>
    /// Binds the world texture (atlas on unit 0) for a world draw.
    /// </summary>
    private void BindWorldTextures()
    {
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, textureAtlas);
    }

    /// <summary>
    /// Pushes the per-frame lighting state plus this draw's model matrix to the world
    /// shader. The vertex shader needs uModel for world positions and normals.
    /// </summary>
    private void ApplyLightingUniforms(Matrix4x4 model)
    {
        worldShader.SetUniform("uModel", model);
        worldShader.SetUniform("uTime", _time);
        worldShader.SetUniform("uSunDir", _sunDir);
        worldShader.SetUniform("uSunColor", _sunColor);
        worldShader.SetUniform("uSunIntensity", _sunIntensity);
        worldShader.SetUniform("uAmbient", _ambient);
        worldShader.SetUniform("uTorchColor", _torchColor);
        worldShader.SetUniform("uTorchIntensity", TorchIntensity);
        worldShader.SetUniform("uFogColor", _fogColor);
        worldShader.SetUniform("uFogNear", WorldGenerationConfig.FogNearBlocks);
        worldShader.SetUniform("uFogFar", WorldGenerationConfig.FogFarBlocks);
        worldShader.SetUniform("uCameraPos", _cameraPos);
        worldShader.SetUniform("uNumPointLights", _pointCount);
        worldShader.SetUniformArray3("uPointPos", _pointPos);
        worldShader.SetUniformArray3("uPointColor", _pointColors);
        worldShader.SetUniform("uSwayStrength", WorldGenerationConfig.LeafSwayStrength);
        worldShader.SetUniform("uFlash", 0f);
    }

    // Reused across frames to avoid a per-frame allocation
    private readonly List<(Chunk chunk, float distSq)> _visibleChunks = new(1024);

    public void RenderWorld(Camera camera, IEnumerable<Chunk> chunks, double time)
    {
        if (camera == null) return;

        worldShader.Use();
        BindWorldTextures();

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

            var model = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size);
            ApplyLightingUniforms(model);
            worldShader.SetUniform("uMVP", model * viewProj);
            worldShader.SetUniform("uAlpha", MathF.Min(1F, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

            chunk.Render();
        }
    }

    private readonly List<(LodRegion region, float distSq)> _visibleRegions = new(256);

    /// <summary>
    /// Draws far-terrain LOD regions. Called before RenderWorld so full-detail chunks
    /// draw over the LOD wherever both exist (the LOD sits strictly below the real surface).
    /// </summary>
    public void RenderLod(Camera camera, IEnumerable<LodRegion> regions)
    {
        if (camera == null) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var frustum = new Frustum(viewProj);
        var camPos = camera.Position;

        _visibleRegions.Clear();

        foreach (var region in regions)
        {
            // Buried regions are fully covered by a finer level (or, for the finest level, by
            // drawn chunks); drawing them too would stack shells visible from inside caves
            if (!region.HasMesh || region.IsBuried)
                continue;

            var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
            var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            var center = (min + max) * 0.5f;
            _visibleRegions.Add((region, Vector3.DistanceSquared(center, camPos)));
        }

        // Coarsest level first, then front to back within a level; where levels still
        // overlap (a coarse region only partly covered by finer ones) finer geometry
        // draws over the coarser mesh buried beneath it
        _visibleRegions.Sort((a, b) => a.region.Level != b.region.Level
            ? b.region.Level.CompareTo(a.region.Level)
            : a.distSq.CompareTo(b.distSq));

        foreach (var (region, _) in _visibleRegions)
        {
            region.Lifetime += 1.0 / 60.0;

            var model = Matrix4x4.CreateTranslation(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks);
            ApplyLightingUniforms(model);
            worldShader.SetUniform("uMVP", model * viewProj);
            worldShader.SetUniform("uAlpha", MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

            region.Render();
        }
    }

    /// <summary>
    /// Draws a single block as a standalone cube scaled about its center. Used for the
    /// mining pop: at scale > 1 the cube encloses the block baked into the chunk mesh, so
    /// the block appears to jump out of the terrain.
    /// </summary>
    public void RenderBlockPop(Camera camera, Vector3 blockPosition, byte blockId, float scale)
    {
        var mesh = blockCubes.Get(blockId);
        if (mesh == null) return;

        worldShader.Use();
        BindWorldTextures();

        // The cube is centred on its own origin, so scaling happens about the block's centre
        var model = Matrix4x4.CreateScale(scale)
                    * Matrix4x4.CreateTranslation(blockPosition + new Vector3(0.5f));

        ApplyLightingUniforms(model);
        worldShader.SetUniform("uMVP", model * camera.GetViewProjection());
        worldShader.SetUniform("uAlpha", 1f);

        mesh.Render();
    }

    /// <summary>
    /// Draws the floating item drops, each spinning about its own centre and pulsing in scale.
    /// </summary>
    public void RenderItemDrops(Camera camera, IReadOnlyList<ItemDrop> drops)
    {
        if (camera == null || drops.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();

        foreach (var drop in drops)
        {
            var mesh = blockCubes.Get(drop.BlockId);
            if (mesh == null) continue;

            // The cube is centred on its own origin, so it spins in place
            var model = Matrix4x4.CreateScale(drop.Scale)
                        * Matrix4x4.CreateRotationY(drop.Spin)
                        * Matrix4x4.CreateTranslation(drop.Position);

            ApplyLightingUniforms(model);
            worldShader.SetUniform("uMVP", model * viewProj);
            worldShader.SetUniform("uAlpha", 1f);
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws the primed TNT cubes at full block size, strobing white through uFlash as their
    /// fuses burn down. Leaves uFlash at 0 so the other world draws stay textured.
    /// </summary>
    public void RenderPrimedTnt(Camera camera, IReadOnlyList<PrimedTnt> primed)
    {
        if (camera == null || primed.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();

        foreach (var tnt in primed)
        {
            var mesh = blockCubes.Get(BlockType.Tnt);
            if (mesh == null) continue;

            // The cube is centred on its own origin, so it scales about its centre
            var model = Matrix4x4.CreateScale(tnt.Scale)
                        * Matrix4x4.CreateTranslation(tnt.Position);

            ApplyLightingUniforms(model);
            worldShader.SetUniform("uMVP", model * viewProj);
            worldShader.SetUniform("uAlpha", 1f);
            worldShader.SetUniform("uFlash", tnt.WhiteFlash);
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws a felled tree swinging around its base. The mesh uploads on first sight (GL is
    /// only legal on this thread); an empty mesh means nothing visible was collected.
    /// </summary>
    public void RenderTopplingTree(Camera camera, TopplingTree tree)
    {
        if (camera == null || tree == null) return;

        tree.EnsureMesh(gl);
        var mesh = tree.Mesh;
        if (mesh == null) return;

        worldShader.Use();
        BindWorldTextures();

        var model = tree.Model;
        ApplyLightingUniforms(model);
        worldShader.SetUniform("uMVP", model * camera.GetViewProjection());
        worldShader.SetUniform("uAlpha", 1f);

        mesh.Render();
    }

    /// <summary>
    /// Draws the targeted block's outline and face highlight. <paramref name="scale"/> is the
    /// block's current pop scale so the highlight tracks it while mining.
    /// </summary>
    public void RenderBlockSelection(Camera camera, Vector3 blockPosition, Vector3 hitNormal, float scale = 1f)
    {
        selectionShader.Use();

        var viewProj = camera.GetViewProjection();
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        var center = blockPosition + new Vector3(0.5f);
        var outlineModel = Matrix4x4.CreateScale(1.01f * scale) * Matrix4x4.CreateTranslation(center);
        selectionShader.SetUniform("uMVP", outlineModel * viewProj);
        selectionShader.SetUniform("uColor", new Vector3(1.0f, 0.95f, 0.35f));
        selectionShader.SetUniform("uOpacity", 0.95f);

        gl.LineWidth(2f);
        gl.BindVertexArray(outlineVao);
        unsafe { gl.DrawElements(PrimitiveType.Lines, 24, DrawElementsType.UnsignedInt, null); }

        var faceIndex = FaceIndexFromNormal(hitNormal);
        if (faceIndex >= 0)
        {
            // Face geometry is local 0..1, so scale about the block center, then nudge the
            // quad off the (now possibly moved) face so it never z-fights the block
            var faceModel = Matrix4x4.CreateTranslation(new Vector3(-0.5f))
                            * Matrix4x4.CreateScale(scale)
                            * Matrix4x4.CreateTranslation(center + hitNormal * 0.003f);
            selectionShader.SetUniform("uMVP", faceModel * viewProj);
            selectionShader.SetUniform("uColor", new Vector3(1.0f, 1.0f, 0.5f));
            selectionShader.SetUniform("uOpacity", 0.3f);

            gl.BindVertexArray(faceVao);
            gl.DrawArrays(GLEnum.TriangleFan, faceIndex * 4, 4);
        }

        gl.BindVertexArray(0);
        gl.DepthMask(true);
    }

    /// <summary>
    /// Draws the block-breaking progress overlay on all faces of the targeted block:
    /// a dark fill with a bright leading edge grows from the face centers as progress
    /// goes 0 -> 1 (see break-fragment.glsl). <paramref name="scale"/> is the block's
    /// current pop scale so the overlay tracks it while mining.
    /// </summary>
    public void RenderBlockBreaking(Camera camera, Vector3 blockPosition, float progress, float scale = 1f)
    {
        if (progress <= 0f) return;

        breakShader.Use();
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Inflate slightly around the block center so the overlay never z-fights the block
        var center = new Vector3(0.5f);
        var model = Matrix4x4.CreateTranslation(-center)
                    * Matrix4x4.CreateScale(1.004f * scale)
                    * Matrix4x4.CreateTranslation(blockPosition + center);
        breakShader.SetUniform("uMVP", model * camera.GetViewProjection());
        breakShader.SetUniform("uProgress", progress);

        gl.BindVertexArray(faceVao);
        for (var face = 0; face < 6; face++)
            gl.DrawArrays(GLEnum.TriangleFan, face * 4, 4);

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

    public void RenderUI(IWindow window, Inventory inventory)
    {
        ui.Begin(window.Size);
        ui.DrawCrosshair();
        ui.DrawHotbar(inventory);
        ui.End();
    }

    public void Dispose()
    {
        ui.Dispose();
        blockCubes.Dispose();
        worldShader.Dispose();
        selectionShader.Dispose();
        breakShader.Dispose();
        gl.DeleteBuffer(outlineVbo);
        gl.DeleteBuffer(outlineEbo);
        gl.DeleteVertexArray(outlineVao);
        gl.DeleteBuffer(faceVbo);
        gl.DeleteVertexArray(faceVao);
        gl.Dispose();
    }
}
