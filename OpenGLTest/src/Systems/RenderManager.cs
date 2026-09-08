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
    private Shader shadowShader;
    private Shader sunShader;
    private UIRenderer ui;
    private BlockCubeCache blockCubes;
    private ItemMeshCache itemMeshes;
    private uint textureAtlas;
    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    private uint faceVao;
    private uint faceVbo;

    // The sun disc: one shared unit quad plus its sprite, oriented around _sunDir every frame
    // by RenderSun. _sunVisibility fades it out below the horizon and underwater.
    private uint _sunTexture;
    private Mesh _sunQuad;
    private float _sunVisibility;

    // Directional sun shadows: RenderShadows fills _shadowDepthTex (a depth-only FBO
    // attachment, sampled as a comparison sampler on unit 1) through _lightViewProj, and
    // every world draw hands both to test-fragment.glsl's SampleShadow.
    private uint _shadowFbo;
    private uint _shadowDepthTex;
    private Vector2D<int> _viewportSize;

    // Planar water reflections: the opaque world mirrored across the sea surface, drawn
    // once per frame at WaterReflectionScale resolution. Sampled projectively by water
    // fragments; the depth texture is a render target only, never sampled.
    private uint _reflFbo;
    private uint _reflTex;
    private uint _reflDepthTex;
    private Vector2D<int> _reflSize;
    private Matrix4x4 _reflViewProj = Matrix4x4.Identity;

    // The sea surface the reflection mirrors across (a source cell's top), and the
    // per-frame clip that keeps below-plane geometry out of the reflection texture.
    // _clipY rides ApplyFrameUniforms into uClipY; -1e9 disables the discard.
    private const float WaterPlaneY = WorldGenerationConfig.SeaLevel + FluidState.SourceHeight;
    private float _clipY = -1e9f;

    // Per-frame lighting state, pushed once per frame via SetLighting and uploaded to the
    // world shader once per frame by ApplyFrameUniforms
    private Vector3 _sunDir = Vector3.UnitY;
    private Vector3 _sunColor = Vector3.One;
    private float _sunIntensity = 1f;
    private Vector3 _ambient = new(0.42f, 0.45f, 0.52f);
    private readonly Vector3 _torchColor = new(1f, 0.62f, 0.25f);
    private const float TorchIntensity = 1.2f;
    private Vector3 _fogColor = new(0.3f, 0.6f, 0.9f);
    private Vector3 _skyColor = new(0.3f, 0.6f, 0.9f);
    private float _fogNear = WorldGenerationConfig.FogNearBlocks;
    private float _fogFar = WorldGenerationConfig.FogFarBlocks;
    private Vector3 _cameraPos;
    private float _time;

    // 0 at night (and fully underwater), 1 with the sun well up: how much of the direct
    // sun term the shadow lookup is allowed to take away
    private float _shadowStrength;
    private Matrix4x4 _lightViewProj = Matrix4x4.Identity;

    // Underwater tint: the eye state is a hard flag, so it is eased into _underwaterT
    // (0 = above water, 1 = fully submerged) over UnderwaterFadeSeconds
    private static readonly Vector3 UnderwaterFogColor = new(0.05f, 0.22f, 0.42f);
    private static readonly Vector3 UnderwaterAmbientTint = new(0.55f, 0.75f, 1f);
    private double _prevTime;
    private float _underwaterT;

    // Whole-frame GPU time, measured with a ring of TIME_ELAPSED queries so a result is only
    // ever read once the driver has it (see BeginGpuFrame). Three slots is enough for the
    // usual one-to-two frame latency.
    private readonly uint[] _gpuQueries = new uint[3];
    private readonly bool[] _gpuQueryPending = new bool[3];
    private int _gpuQueryIndex;
    private bool _gpuQueryActive;

    /// <summary>Chunks the last <see cref="RenderWorld"/> call passed the frustum test.</summary>
    public int LastVisibleChunkCount { get; private set; }

    private readonly Vector3[] _pointPos = new Vector3[WorldGenerationConfig.MaxPointLights];
    private readonly Vector3[] _pointColors = new Vector3[WorldGenerationConfig.MaxPointLights];
    private int _pointCount;

    // World-shader uniform locations, resolved once in LoadResources. The first four are the
    // only uniforms a world draw still touches, so the per-draw cost is three (four for TNT)
    // glUniform calls with no dictionary lookup in between; the arrays feed ApplyFrameUniforms.
    private int _locModel;
    private int _locMvp;
    private int _locAlpha;
    private int _locFlash;
    private int _locAlphaCutoff;
    private int[] _locPointPos = [];
    private int[] _locPointColor = [];

    public RenderManager(IWindow window)
    {
        gl = window.CreateOpenGL();
        gl.Enable(GLEnum.DepthTest);
        gl.DepthFunc(GLEnum.Lequal);
        _viewportSize = window.Size;

        // Which GPU actually ran matters for every timing: a hybrid laptop happily renders
        // this on the iGPU unless told otherwise, and the numbers then mean nothing.
        Console.WriteLine($"GPU: {gl.GetStringS(StringName.Renderer)} | {gl.GetStringS(StringName.Vendor)} | GL {gl.GetStringS(StringName.Version)}");

        for (var i = 0; i < _gpuQueries.Length; i++)
            _gpuQueries[i] = gl.GenQuery();

        LoadResources();
    }

    /// <summary>
    /// Opens this frame's GPU timer and, before reusing the slot, drains whatever result is
    /// already sitting in it. The query is only read when the driver says it is available, so
    /// this never stalls the pipeline; a result that is still missing after a full lap of the
    /// ring is simply dropped. Only one TIME_ELAPSED query may be active at a time, which is
    /// why this wraps the whole frame instead of individual passes.
    /// </summary>
    public void BeginGpuFrame()
    {
        var id = _gpuQueries[_gpuQueryIndex];

        if (_gpuQueryPending[_gpuQueryIndex])
        {
            gl.GetQueryObject(id, QueryObjectParameterName.ResultAvailable, out int available);
            if (available != 0)
            {
                gl.GetQueryObject(id, QueryObjectParameterName.Result, out long nanoseconds);
                FrameProfiler.ReportGpuNanoseconds(nanoseconds);
            }

            _gpuQueryPending[_gpuQueryIndex] = false;
        }

        gl.BeginQuery(QueryTarget.TimeElapsed, id);
        _gpuQueryActive = true;
    }

    /// <summary>Closes the frame's GPU timer and moves the ring on.</summary>
    public void EndGpuFrame()
    {
        if (!_gpuQueryActive) return;

        gl.EndQuery(QueryTarget.TimeElapsed);
        _gpuQueryActive = false;
        _gpuQueryPending[_gpuQueryIndex] = true;
        _gpuQueryIndex = (_gpuQueryIndex + 1) % _gpuQueries.Length;
    }

    private void LoadResources()
    {
        // Blocks, items, loot and models first: everything below (and the World that follows)
        // reads the tables this fills, and nothing in it touches GL or the atlas.
        ContentRegistry.Load("assets");

        var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
        var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
        var selectionVs = FileUtils.ReadFile("assets/shaders/selection-vertex.glsl");
        var selectionFs = FileUtils.ReadFile("assets/shaders/selection-fragment.glsl");
        var breakVs = FileUtils.ReadFile("assets/shaders/break-vertex.glsl");
        var breakFs = FileUtils.ReadFile("assets/shaders/break-fragment.glsl");
        var shadowVs = FileUtils.ReadFile("assets/shaders/shadow-vertex.glsl");
        var shadowFs = FileUtils.ReadFile("assets/shaders/shadow-fragment.glsl");
        var sunVs = FileUtils.ReadFile("assets/shaders/sun-vertex.glsl");
        var sunFs = FileUtils.ReadFile("assets/shaders/sun-fragment.glsl");

        textureAtlas = TextureAtlas.Build(gl, "assets/textures/blocks");
        ContentRegistry.BakeModels();

        worldShader = new Shader(gl, vs, fs);
        // SetUniform writes to the currently bound program, so bind first — otherwise
        // the sampler unit silently stays at its default.
        worldShader.Use();
        worldShader.SetUniform("uTextureAtlas", 0);
        worldShader.SetUniform("uShadowMap", 1);
        worldShader.SetUniform("uReflectionMap", 2);
        worldShader.SetUniform("uWaterPlaneY", WaterPlaneY);
        worldShader.SetUniform("uReflValid", 0f);
        CreateReflectionTarget();

        // Everything a world draw sets per draw, resolved once here (see SetDrawUniforms)
        _locModel = worldShader.GetUniformLocation("uModel");
        _locMvp = worldShader.GetUniformLocation("uMVP");
        _locAlpha = worldShader.GetUniformLocation("uAlpha");
        _locFlash = worldShader.GetUniformLocation("uFlash");
        _locAlphaCutoff = worldShader.GetUniformLocation("uAlphaCutoff");
        _locPointPos = worldShader.GetUniformArrayLocations("uPointPos", WorldGenerationConfig.MaxPointLights);
        _locPointColor = worldShader.GetUniformArrayLocations("uPointColor", WorldGenerationConfig.MaxPointLights);

        selectionShader = new Shader(gl, selectionVs, selectionFs);
        breakShader = new Shader(gl, breakVs, breakFs);

        shadowShader = new Shader(gl, shadowVs, shadowFs);
        // The depth pass still samples the atlas, to punch the cutout tiles' holes
        shadowShader.Use();
        shadowShader.SetUniform("uTextureAtlas", 0);
        CreateShadowMap();

        sunShader = new Shader(gl, sunVs, sunFs);
        sunShader.Use();
        sunShader.SetUniform("uSunTexture", 0);

        _sunTexture = TextureLoader.LoadFromFile(gl, "assets/textures/environment/sun.png");
        // The loader hard-codes Nearest for the block tiles; the 64px disc is scaled up a lot
        // on screen, so it needs Linear or its edge turns into a staircase.
        gl.BindTexture(TextureTarget.Texture2D, _sunTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        _sunQuad = Primitives.CreateSquare(gl, 1f);

        blockCubes = new BlockCubeCache(gl);
        itemMeshes = new ItemMeshCache(gl);
        ui = new UIRenderer(gl, textureAtlas, blockCubes);
        CreateSelectionGeometry();
    }

    /// <summary>
    /// Allocates the shadow map: one depth texture, no colour attachment. It is sampled as a
    /// <c>sampler2DShadow</c>, so the compare mode does the depth test in hardware and Linear
    /// filtering gives free 2x2 bilinear PCF on top of the shader's own kernel. Outside the
    /// ortho box the border colour reads as depth 1 (farthest), i.e. fully lit.
    /// </summary>
    private void CreateShadowMap()
    {
        const uint size = WorldGenerationConfig.ShadowMapSize;

        _shadowDepthTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _shadowDepthTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                size, size, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
        Span<float> border = [1f, 1f, 1f, 1f];
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)GLEnum.CompareRefToTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);

        _shadowFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _shadowDepthTex, 0);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.ReadBuffer(ReadBufferMode.None);

        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Shadow framebuffer incomplete: {status}");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void SetViewport(Vector2D<int> size)
    {
        _viewportSize = size;
        gl.Viewport(size);
        CreateReflectionTarget();
    }

    /// <summary>
    /// Advances the day/night cycle and pushes the resulting sun, sky and point-light
    /// state into the per-frame fields. Called once per frame before <see cref="BeginFrame"/>,
    /// which uploads the result to the world shader through <see cref="ApplyFrameUniforms"/>.
    /// <paramref name="underwater"/> is whether the camera's eye sits below a fluid surface;
    /// it fades the underwater tint in and out.
    /// </summary>
    public void SetLighting(double time, Camera? camera, IReadOnlyList<PrimedTnt>? primed, bool underwater)
    {
        // The window clock is the only time source this pass has; clamp the step so a hitch
        // (or the very first frame) cannot jump the fade
        var dt = (float)Math.Clamp(time - _prevTime, 0.0, 0.25);
        _prevTime = time;

        _time = (float)time;
        if (camera != null)
            _cameraPos = camera.Position;

        // The sun orbits once per DayLengthSeconds; the phase starts it mid-morning
        var angle = (float)(time / WorldGenerationConfig.DayLengthSeconds) * MathF.Tau + 0.8f;
        _sunDir = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        var elevation = _sunDir.Y;

        // The shadow camera fades itself out as the sun sets: at a grazing elevation the
        // shadows stretch to the horizon and the ortho box cannot hold them, and at night
        // there is no direct sun left to remove anyway.
        _shadowStrength = WorldGenerationConfig.ShadowsEnabled
            ? Smooth01((elevation - 0.02f) / 0.12f)
            : 0f;

        // The disc itself fades over a much narrower band than the light does, so it sinks
        // out of sight shortly after the horizon instead of hanging in the night sky
        _sunVisibility = Smooth01((elevation + 0.08f) / 0.14f);

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

        // Underwater the fog closes in to a few blocks, the sky and fog go blue-green and the
        // light dims and cools, so a submerged view reads as being inside the water rather
        // than looking at a blue filter. Scaled by dayness so night water stays dark.
        _underwaterT = MathUtils.Clamp01(
            _underwaterT + (underwater ? dt : -dt) / WorldGenerationConfig.UnderwaterFadeSeconds);
        var wet = MathUtils.SmoothStep(_underwaterT);

        // Underwater the sun is a diffuse glow, so the crisp shadow edges go with the tint.
        // This has to run before the early return below, or a submerged view would keep them.
        _shadowStrength *= 1f - wet;
        // Same reason the disc goes with them: from under the surface there is no sun to see
        _sunVisibility *= 1f - wet;
        UpdateShadowCamera(camera);

        _fogNear = MathUtils.Lerp(WorldGenerationConfig.FogNearBlocks,
            WorldGenerationConfig.UnderwaterFogNearBlocks, wet);
        _fogFar = MathUtils.Lerp(WorldGenerationConfig.FogFarBlocks,
            WorldGenerationConfig.UnderwaterFogFarBlocks, wet);

        if (wet <= 0f) return;

        var underwaterColor = UnderwaterFogColor * (0.15f + 0.85f * dayness);
        _fogColor = Mix(_fogColor, underwaterColor, wet);
        _skyColor = Mix(_skyColor, underwaterColor, wet);
        _ambient = Mix(_ambient, _ambient * UnderwaterAmbientTint, wet);
        _sunIntensity = MathUtils.Lerp(_sunIntensity, _sunIntensity * 0.5f, wet);
    }

    /// <summary>
    /// Builds the light-space matrix for this frame: an orthographic box of half-extent
    /// <see cref="WorldGenerationConfig.ShadowRadiusBlocks"/> centred a little ahead of the
    /// camera (most of what the player looks at is in front of them) and looking down the sun
    /// direction. The box centre is snapped to whole shadow texels in light space, or the
    /// sub-texel drift as the player walks makes every shadow edge crawl and shimmer.
    /// </summary>
    private void UpdateShadowCamera(Camera? camera)
    {
        if (camera == null || _shadowStrength <= 0f) return;

        const float r = WorldGenerationConfig.ShadowRadiusBlocks;
        const float d = 2.5f * r;
        var target = camera.Position + camera.Forward * (r * 0.35f);

        // Snap in the provisional light space, then bring the snapped point back to world
        // space and rebuild the view around it
        var lightView = Matrix4x4.CreateLookAt(target + _sunDir * d, target, Vector3.UnitY);
        var texel = 2f * r / WorldGenerationConfig.ShadowMapSize;
        var inLight = Vector3.Transform(target, lightView);
        inLight.X = MathF.Round(inLight.X / texel) * texel;
        inLight.Y = MathF.Round(inLight.Y / texel) * texel;
        var snappedTarget = Matrix4x4.Invert(lightView, out var inverseView)
            ? Vector3.Transform(inLight, inverseView)
            : target;

        lightView = Matrix4x4.CreateLookAt(snappedTarget + _sunDir * d, snappedTarget, Vector3.UnitY);
        // The sun's 0.35 Z tilt keeps it more than 19 degrees off vertical, so UnitY is
        // never parallel to the view direction and the look-at stays well defined
        var lightProj = Matrix4x4.CreateOrthographic(2f * r, 2f * r, d - 2f * r, d + 2f * r);
        _lightViewProj = lightView * lightProj;
    }

    /// <summary>
    /// Depth-only pass that fills the shadow map with every caster inside the light's ortho
    /// box: opaque chunk meshes, primed TNT and toppling trees. Runs first each frame, before
    /// anything draws to the default framebuffer; water never casts. Culling uses the light
    /// frustum, so the caster set is exactly what the box can hold.
    /// <paramref name="chunks"/> is <see cref="ChunkManager.Renderable"/>, a superset of the
    /// casters: water-only chunks are in it and are skipped here.
    /// </summary>
    public void RenderShadows(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<PrimedTnt> primed, IReadOnlyList<FallingBlock> falling, IEnumerable<TopplingTree> trees)
    {
        if (camera == null || _shadowStrength <= 0f) return;

        const uint size = WorldGenerationConfig.ShadowMapSize;

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        gl.Viewport(0, 0, size, size);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        // No colour is written, so blending would only cost fill rate
        gl.Disable(EnableCap.Blend);

        shadowShader.Use();
        // The depth texture is this pass's render target, so it must not stay bound to unit 1
        // while it draws: sampling and rendering the same texture is a feedback loop, and
        // drivers are free to return garbage for it. BindWorldTextures rebinds it afterwards.
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, textureAtlas);
        shadowShader.SetUniform("uTime", _time);
        shadowShader.SetUniform("uSwayStrength", WorldGenerationConfig.LeafSwayStrength);

        var frustum = new Frustum(_lightViewProj);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            // HasMesh is the opaque mesh only, so a water-only chunk (translucent mesh and
            // nothing else) never enters the depth pass — fluids are not casters anyway.
            if (!chunk.HasMesh) continue;

            // Cull buried casters. The light's ortho box is 180 blocks wide and 360 deep, so
            // its frustum admits every chunk of a column right down to bedrock, and a chunk
            // whose top sits below the lowest surface height in its own 16x16 column is
            // completely under the terrain: no sunlight reaches it, so it can occlude nothing.
            // A 1-block margin keeps the surface block itself (and the face under it) in.
            // Trees do poke above the surface, but only into the chunk ABOVE, whose own top is
            // then above that column's surface height and so is never skipped by this test.
            // (Written as top + 1 < min rather than top < min - 1 so the int.MinValue
            // sentinel on a chunk with no block data cannot wrap and cull everything.)
            if (chunk.UpperBoundY + 1 < chunk.SurfaceMinY) continue;

            var min = chunk.Position * Chunk.Size;
            var max = min + new Vector3(Chunk.Size);

            if (!frustum.IntersectsAABB(min, max)) continue;

            var model = Matrix4x4.CreateTranslation(min);
            shadowShader.SetUniform("uModel", model);
            shadowShader.SetUniform("uLightMVP", model * _lightViewProj);
            chunk.Render();
        }

        foreach (var tnt in primed)
        {
            var mesh = blockCubes.Get(tnt.BlockId);
            if (mesh == null) continue;

            var model = Matrix4x4.CreateScale(tnt.Scale)
                        * Matrix4x4.CreateTranslation(tnt.Position);
            shadowShader.SetUniform("uModel", model);
            shadowShader.SetUniform("uLightMVP", model * _lightViewProj);
            mesh.Render();
        }

        foreach (var block in falling)
        {
            var mesh = blockCubes.Get(block.BlockId);
            if (mesh == null) continue;

            var model = Matrix4x4.CreateTranslation(block.Position);
            shadowShader.SetUniform("uModel", model);
            shadowShader.SetUniform("uLightMVP", model * _lightViewProj);
            mesh.Render();
        }

        foreach (var tree in trees)
        {
            tree.EnsureMesh(gl);
            var mesh = tree.Mesh;
            if (mesh == null) continue;

            var model = tree.Model;
            shadowShader.SetUniform("uModel", model);
            shadowShader.SetUniform("uLightMVP", model * _lightViewProj);
            mesh.Render();
        }

        // Back to the default framebuffer and the blend state the world passes expect
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(_viewportSize);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    /// <summary>
    /// Builds the half-res reflection texture: a colour target plus a depth texture, sized
    /// from the viewport. Called from <see cref="LoadResources"/> and
    /// <see cref="SetViewport"/>; recreating only when the size actually changed.
    /// </summary>
    private void CreateReflectionTarget()
    {
        var w = Math.Max(2, (int)(_viewportSize.X * WorldGenerationConfig.WaterReflectionScale));
        var h = Math.Max(2, (int)(_viewportSize.Y * WorldGenerationConfig.WaterReflectionScale));
        if (_reflFbo != 0 && _reflSize.X == w && _reflSize.Y == h) return;

        DeleteReflectionTarget();
        _reflSize = new Vector2D<int>(w, h);

        _reflTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _reflTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _reflDepthTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _reflDepthTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        _reflFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _reflTex, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _reflDepthTex, 0);

        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Reflection framebuffer incomplete: {status}");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void DeleteReflectionTarget()
    {
        if (_reflFbo == 0) return;
        gl.DeleteFramebuffer(_reflFbo);
        gl.DeleteTexture(_reflTex);
        gl.DeleteTexture(_reflDepthTex);
        _reflFbo = 0;
    }

    /// <summary>
    /// Renders the opaque world (chunks + LOD, no water, no entities) from a camera
    /// mirrored across the sea surface into the reflection texture, which the water in
    /// <see cref="RenderWorldTranslucent"/> then samples projectively. Below-plane
    /// geometry is clipped via <c>uClipY</c> so the seabed never shows up in the mirror.
    /// Skipped (leaving <c>uReflValid</c> at 0) when there is no water to reflect in or
    /// the eye itself is below the surface. Runs after <see cref="RenderShadows"/> so the
    /// shadow map it samples is already filled.
    /// </summary>
    public void RenderReflection(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<LodRegion> regions)
    {
        worldShader.Use();
        if (!WorldGenerationConfig.WaterEnabled || camera == null
            || camera.Position.Y < WaterPlaneY || _reflFbo == 0)
        {
            worldShader.SetUniform("uReflValid", 0f);
            return;
        }

        // The reflection texture is this pass's render target, so it must not stay bound
        // to unit 2 while it draws: sampling and rendering the same texture is a
        // feedback loop (see RenderShadows for unit 1). Later passes rebind it.
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.ActiveTexture(TextureUnit.Texture0);

        // Mirror position and facing across the horizontal plane; pitch stays within
        // ±89° through the mirror, so UnitY never parallels the view direction.
        var fwd = camera.Forward;
        var mirroredPos = new Vector3(camera.Position.X, 2f * WaterPlaneY - camera.Position.Y,
            camera.Position.Z);
        var mirroredFwd = new Vector3(fwd.X, -fwd.Y, fwd.Z);
        var view = Matrix4x4.CreateLookAt(mirroredPos, mirroredPos + mirroredFwd, Vector3.UnitY);
        _reflViewProj = view * camera.GetProjectionMatrix();

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflFbo);
        gl.Viewport(_reflSize);
        gl.ClearColor(_skyColor.X, _skyColor.Y, _skyColor.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Disable(EnableCap.Blend);

        // Fog and specular inside the texture want the mirrored eye; the clip drops
        // everything below the surface. Both are restored below.
        var savedCamPos = _cameraPos;
        var savedClip = _clipY;
        _cameraPos = mirroredPos;
        _clipY = WaterPlaneY;
        ApplyFrameUniforms();
        worldShader.SetUniform("uReflViewProj", _reflViewProj);

        var frustum = new Frustum(_reflViewProj);

        _visibleChunks.Clear();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (!chunk.HasMesh) continue;

            var min = chunk.Position * Chunk.Size;
            var max = min + new Vector3(Chunk.Size);

            if (!frustum.IntersectsAABB(min, max)) continue;

            _visibleChunks.Add((chunk, Vector3.DistanceSquared(min, mirroredPos)));
        }

        _visibleChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        foreach (var (chunk, _) in _visibleChunks)
        {
            var model = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size);
            SetDrawUniforms(model, model * _reflViewProj,
                MathF.Min(1f, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));
            chunk.Render();
        }

        _visibleRegions.Clear();
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if (!region.HasMesh || region.IsBuried) continue;

            var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
            var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);

            if (!frustum.IntersectsAABB(min, max)) continue;

            _visibleRegions.Add((region, 0f));
        }

        foreach (var (region, _) in _visibleRegions)
        {
            var model = Matrix4x4.CreateTranslation(region.X * region.SizeBlocks, 0f,
                region.Z * region.SizeBlocks);
            SetDrawUniforms(model, model * _reflViewProj,
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));
            region.Render();
        }

        _cameraPos = savedCamPos;
        _clipY = savedClip;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(_viewportSize);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Restores the real eye's fog/camera uniforms; uReflViewProj survives in the program.
        ApplyFrameUniforms();
        worldShader.SetUniform("uReflValid", 1f);
    }

    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector3 Mix(Vector3 a, Vector3 b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    /// <summary>
    /// Clears to the sky colour, paints the sun into it, and leaves the GL state the world
    /// passes expect. <paramref name="camera"/> only orients the sun quad; a null camera just
    /// skips it.
    /// </summary>
    public void BeginFrame(Camera? camera)
    {
        gl.ClearColor(_skyColor.X, _skyColor.Y, _skyColor.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Straight onto the fresh sky, before any terrain: see RenderSun
        RenderSun(camera);

        worldShader.Use();
        // Opaque default: punch the leaf tiles' holes; the translucent pass lowers it.
        worldShader.SetUniform(_locAlphaCutoff, 0.5f);
        ApplyFrameUniforms();
        gl.Enable(EnableCap.Blend);
        // The world passes rely on straight alpha blending for the fluid mesh and the chunk
        // fade-in. Set it here rather than inheriting whatever the last UI pass (or the sun's
        // additive blend above) left behind.
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    /// <summary>
    /// Draws the sun: a textured disc surrounded by a radial glow, on one screen-facing quad.
    /// Three things make it cheap. It is drawn first, with the depth test and depth writes
    /// off, so everything drawn afterwards occludes it for free and the sky needs no skybox.
    /// The quad is placed 100 blocks along <see cref="_sunDir"/> under a view matrix with the
    /// translation removed, so it effectively sits at infinity and never parallaxes as the
    /// player walks. And it blends additively (One, One), so the shader's black background
    /// contributes nothing and the glow brightens the sky rather than greying it.
    /// </summary>
    private void RenderSun(Camera? camera)
    {
        if (camera == null || _sunVisibility <= 0f) return;

        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        sunShader.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sunTexture);

        // Rotation only: dropping the camera position is what pins the quad at infinity
        var viewRot = Matrix4x4.CreateLookAt(Vector3.Zero, camera.Forward, Vector3.UnitY);
        sunShader.SetUniform("uViewProj", viewRot * camera.GetProjectionMatrix());
        sunShader.SetUniform("uSunDir", _sunDir);
        // The quad has to hold the whole glow, so it is sized to the glow's angular radius and
        // the shader keeps the textured disc inside the inner uDiscFraction of it. The unit
        // square's corners sit at +-0.5, hence the factor 2 to make uHalfSize the true radius.
        sunShader.SetUniform("uHalfSize", 2f * 100f * MathF.Tan(WorldGenerationConfig.SunAngularRadiusDeg
            * WorldGenerationConfig.SunGlowScale * MathF.PI / 180f));
        sunShader.SetUniform("uDiscFraction", 1f / WorldGenerationConfig.SunGlowScale);
        sunShader.SetUniform("uGlowIntensity", WorldGenerationConfig.SunGlowIntensity);
        sunShader.SetUniform("uGlowExponent", WorldGenerationConfig.SunGlowExponent);
        sunShader.SetUniform("uSunColor", _sunColor);
        sunShader.SetUniform("uVisibility", _sunVisibility);

        _sunQuad.Render();

        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>
    /// Binds the world textures for a world draw: the atlas on unit 0, the shadow map on
    /// unit 1 and the reflection texture on unit 2. Unit 0 is left active on the way out, because <see cref="RenderItemDrops"/>
    /// binds an item's own texture assuming that.
    /// </summary>
    private void BindWorldTextures()
    {
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, _shadowDepthTex);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, _reflTex);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, textureAtlas);
    }

    /// <summary>
    /// Pushes every world-shader uniform that is constant for the whole frame: the sun, sky,
    /// fog, camera, point lights and shadow parameters. Called once per frame from
    /// <see cref="BeginFrame"/>, right after the world program is bound — uniform values live
    /// in the program object and survive any number of <c>UseProgram</c> switches, so the
    /// selection, break, sun and UI shaders running in between do not disturb these and every
    /// later world pass in the frame inherits them. Everything it reads is final by then:
    /// <see cref="SetLighting"/> (including <c>_lightViewProj</c>) runs before
    /// <see cref="BeginFrame"/>, and nothing in a world pass writes these fields.
    /// </summary>
    private void ApplyFrameUniforms()
    {
        worldShader.SetUniform("uTime", _time);
        worldShader.SetUniform("uSunDir", _sunDir);
        worldShader.SetUniform("uSunColor", _sunColor);
        worldShader.SetUniform("uSunIntensity", _sunIntensity);
        worldShader.SetUniform("uAmbient", _ambient);
        worldShader.SetUniform("uTorchColor", _torchColor);
        worldShader.SetUniform("uTorchIntensity", TorchIntensity);
        worldShader.SetUniform("uFogColor", _fogColor);
        worldShader.SetUniform("uFogNear", _fogNear);
        worldShader.SetUniform("uFogFar", _fogFar);
        worldShader.SetUniform("uCameraPos", _cameraPos);
        worldShader.SetUniform("uNumPointLights", _pointCount);
        worldShader.SetUniformArray3(_locPointPos, _pointPos);
        worldShader.SetUniformArray3(_locPointColor, _pointColors);
        worldShader.SetUniform("uSwayStrength", WorldGenerationConfig.LeafSwayStrength);
        worldShader.SetUniform(_locFlash, 0f);
        worldShader.SetUniform("uLightViewProj", _lightViewProj);
        worldShader.SetUniform("uShadowStrength", _shadowStrength);
        worldShader.SetUniform("uShadowTexel", 1f / WorldGenerationConfig.ShadowMapSize);
        worldShader.SetUniform("uShadowWorldTexel",
            2f * WorldGenerationConfig.ShadowRadiusBlocks / WorldGenerationConfig.ShadowMapSize);
        worldShader.SetUniform("uShadowSoftness", WorldGenerationConfig.ShadowSoftnessTexels);
        worldShader.SetUniform("uShadowNormalOffset", WorldGenerationConfig.ShadowNormalOffsetTexels);
        worldShader.SetUniform("uClipY", _clipY);
    }

    /// <summary>
    /// Everything a single world draw has to say: its model matrix (the vertex shader needs it
    /// for world positions and normals), its full MVP and its fade alpha. Exactly three
    /// <c>glUniform</c> calls through cached locations — the rest of the world shader's state
    /// was uploaded once by <see cref="ApplyFrameUniforms"/>.
    /// </summary>
    private void SetDrawUniforms(in Matrix4x4 model, in Matrix4x4 mvp, float alpha)
    {
        worldShader.SetUniform(_locModel, model);
        worldShader.SetUniform(_locMvp, mvp);
        worldShader.SetUniform(_locAlpha, alpha);
    }

    // Reused across frames to avoid a per-frame allocation
    private readonly List<(Chunk chunk, float distSq)> _visibleChunks = new(1024);

    /// <summary>
    /// Draws the opaque world. <paramref name="chunks"/> is
    /// <see cref="ChunkManager.Renderable"/>: every chunk with something to draw, opaque or
    /// translucent — the water-only ones still need their fade lifetime advanced here.
    /// </summary>
    public void RenderWorld(Camera camera, IReadOnlyList<Chunk> chunks, double time)
    {
        if (camera == null) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var frustum = new Frustum(viewProj);
        var camChunkPos = (camera.Position / Chunk.Size).Floor();

        // Sort chunks by distance for better cache coherency and early z-rejection
        _visibleChunks.Clear();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            // Renderable holds only chunks with something to draw, but a water-only ocean
            // chunk has no opaque mesh — it is here to have its lifetime advanced, or its
            // fluid mesh would stay at alpha 0 forever.
            if (!chunk.HasMesh && !chunk.HasTranslucentMesh)
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

        LastVisibleChunkCount = _visibleChunks.Count;

        foreach (var (chunk, _) in _visibleChunks)
        {
            chunk.lifetime += 1.0 / 60.0; // TODO: Pass delta time eventually
            if (!chunk.HasMesh) continue;

            var model = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size);
            SetDrawUniforms(model, model * viewProj,
                MathF.Min(1F, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

            chunk.Render();
        }
    }

    // Reused across frames, like _visibleChunks / _visibleRegions
    private readonly List<(Chunk chunk, float distSq)> _translucentChunks = new(256);
    private readonly List<(LodRegion region, float distSq)> _translucentRegions = new(256);

    /// <summary>
    /// Draws every fluid surface — chunk water first, then the far LOD's water sheets — in one
    /// blended pass after the whole opaque world, so terrain, drops and TNT behind the water
    /// show through it. Depth writing stays on: water bodies are at most a couple of layers
    /// deep from any viewpoint, and writing depth is what makes the chunk-vs-LOD overlap at
    /// the full-detail ring resolve by distance instead of by draw order.
    /// <paramref name="chunks"/> is <see cref="ChunkManager.Renderable"/> and
    /// <paramref name="regions"/> is <see cref="LodManager.RegionList"/>; both are supersets
    /// of what this pass draws, so the skips below still do the selecting.
    /// </summary>
    public void RenderWorldTranslucent(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<LodRegion> regions)
    {
        if (camera == null) return;

        worldShader.Use();
        // No cutout here: glass's faint interior must blend instead of discarding away.
        worldShader.SetUniform(_locAlphaCutoff, 0f);
        BindWorldTextures();
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(true);

        var viewProj = camera.GetViewProjection();
        var frustum = new Frustum(viewProj);
        var camChunkPos = (camera.Position / Chunk.Size).Floor();
        var camPos = camera.Position;

        _translucentChunks.Clear();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (!chunk.HasTranslucentMesh)
                continue;

            var min = chunk.Position * Chunk.Size;
            var max = min + new Vector3(Chunk.Size);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            _translucentChunks.Add((chunk, Vector3.DistanceSquared(chunk.Position, camChunkPos)));
        }

        // Back to front, so overlapping surfaces (a pond seen across a sea) blend in the
        // order the eye sees them
        _translucentChunks.Sort((a, b) => b.distSq.CompareTo(a.distSq));

        foreach (var (chunk, _) in _translucentChunks)
        {
            var model = Matrix4x4.CreateTranslation(chunk.Position * Chunk.Size);
            SetDrawUniforms(model, model * viewProj,
                MathF.Min(1f, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

            chunk.RenderTranslucent();
        }

        _translucentRegions.Clear();

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if (!region.HasWaterMesh || region.IsBuried)
                continue;

            var min = new Vector3(region.X * region.SizeBlocks, region.YMin, region.Z * region.SizeBlocks);
            var max = new Vector3(min.X + region.SizeBlocks, region.YMax, min.Z + region.SizeBlocks);

            if (!frustum.IntersectsAABB(min, max))
                continue;

            var center = (min + max) * 0.5f;
            _translucentRegions.Add((region, Vector3.DistanceSquared(center, camPos)));
        }

        // Finest level first: its sheet sits highest (each coarser level sinks by its own
        // height bias), so the duplicates underneath it fail the depth test instead of
        // double-blending the same stretch of sea
        _translucentRegions.Sort((a, b) => a.region.Level != b.region.Level
            ? a.region.Level.CompareTo(b.region.Level)
            : b.distSq.CompareTo(a.distSq));

        foreach (var (region, _) in _translucentRegions)
        {
            var model = Matrix4x4.CreateTranslation(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks);
            SetDrawUniforms(model, model * viewProj,
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

            region.RenderWater();
        }

        // Later passes reuse this program and need the cutout behaviour back.
        worldShader.SetUniform(_locAlphaCutoff, 0.5f);
    }

    private readonly List<(LodRegion region, float distSq)> _visibleRegions = new(256);

    /// <summary>
    /// Draws far-terrain LOD regions. Called before RenderWorld so full-detail chunks
    /// draw over the LOD wherever both exist (the LOD sits strictly below the real surface).
    /// <paramref name="regions"/> is <see cref="LodManager.RegionList"/>, every loaded region;
    /// the buried and mesh-less ones are skipped below.
    /// </summary>
    public void RenderLod(Camera camera, IReadOnlyList<LodRegion> regions)
    {
        if (camera == null) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var frustum = new Frustum(viewProj);
        var camPos = camera.Position;

        _visibleRegions.Clear();

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];

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
            SetDrawUniforms(model, model * viewProj,
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

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

        SetDrawUniforms(model, model * camera.GetViewProjection(), 1f);

        mesh.Render();
    }

    /// <summary>
    /// Draws the floating item drops, each spinning about its own centre and pulsing in scale.
    /// Block drops draw their atlas cube; pure items (see <see cref="ItemType"/>) draw their
    /// extruded sprite with its own texture bound (the shader's sampler just reads unit 0).
    /// </summary>
    public void RenderItemDrops(Camera camera, IReadOnlyList<ItemDrop> drops)
    {
        if (camera == null || drops.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        // Only an item sprite ever takes unit 0 away from the atlas, so the atlas is rebound
        // when one actually did rather than once per drop. Unit 1 (the shadow map) never
        // changes in here.
        var atlasBound = true;

        foreach (var drop in drops)
        {
            Mesh? mesh;
            if (ItemType.IsItem(drop.BlockId))
            {
                if (!itemMeshes.TryGet(drop.BlockId, out mesh, out var itemTexture)) continue;
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, itemTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    BindWorldTextures();
                    atlasBound = true;
                }

                mesh = blockCubes.Get(drop.BlockId);
                if (mesh == null) continue;
            }

            // The shape is centred on its own origin, so it spins in place
            var model = Matrix4x4.CreateScale(drop.Scale)
                        * Matrix4x4.CreateRotationY(drop.Spin)
                        * Matrix4x4.CreateTranslation(drop.Position);

            SetDrawUniforms(model, model * viewProj, 1f);
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
        var drewFlash = false;

        foreach (var tnt in primed)
        {
            var mesh = blockCubes.Get(tnt.BlockId);
            if (mesh == null) continue;

            // The cube is centred on its own origin, so it scales about its centre
            var model = Matrix4x4.CreateScale(tnt.Scale)
                        * Matrix4x4.CreateTranslation(tnt.Position);

            SetDrawUniforms(model, model * viewProj, 1f);
            worldShader.SetUniform(_locFlash, tnt.WhiteFlash);
            mesh.Render();
            drewFlash = true;
        }

        // uFlash is a frame constant for every other pass, and the translucent water pass runs
        // after the entities, so put it back rather than tinting whatever draws next.
        if (drewFlash)
            worldShader.SetUniform(_locFlash, 0f);
    }

    /// <summary>
    /// Draws falling sand/gravel cubes at full block size. Unlike primed TNT there is no
    /// flash or swell, so every cube shares one textured draw.
    /// </summary>
    public void RenderFallingBlocks(Camera camera, IReadOnlyList<FallingBlock> falling)
    {
        if (camera == null || falling.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();

        foreach (var block in falling)
        {
            var mesh = blockCubes.Get(block.BlockId);
            if (mesh == null) continue;

            // The cube is centred on its own origin, like the primed TNT above
            var model = Matrix4x4.CreateTranslation(block.Position);

            SetDrawUniforms(model, model * viewProj, 1f);
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
        SetDrawUniforms(model, model * camera.GetViewProjection(), 1f);

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

    public void RenderUI(IWindow window, Inventory inventory, int fps, bool showProfiler)
    {
        ui.Begin(window.Size);

        // Cheap underwater wash over the whole view: the world fog alone leaves nearby
        // geometry untinted, and this is what sells "the camera is in the water"
        if (_underwaterT > 0.001f)
        {
            var wet = MathUtils.SmoothStep(_underwaterT);
            ui.DrawRect(ui.ScreenCentre, new Vector2(window.Size.X, window.Size.Y),
                Rgba.FromFloat(UnderwaterFogColor.X, UnderwaterFogColor.Y, UnderwaterFogColor.Z, 0.18f * wet));
        }

        ui.DrawCrosshair();
        ui.DrawFps(fps);
        if (showProfiler)
            ui.DrawProfiler(FrameProfiler.Lines);
        ui.DrawHotbar(inventory);
        ui.End();
    }

    public void Dispose()
    {
        ui.Dispose();
        blockCubes.Dispose();
        itemMeshes.Dispose();
        worldShader.Dispose();
        selectionShader.Dispose();
        breakShader.Dispose();
        shadowShader.Dispose();
        sunShader.Dispose();
        _sunQuad.Dispose();
        gl.DeleteTexture(_sunTexture);
        gl.DeleteFramebuffer(_shadowFbo);
        gl.DeleteTexture(_shadowDepthTex);
        DeleteReflectionTarget();
        gl.DeleteBuffer(outlineVbo);
        gl.DeleteBuffer(outlineEbo);
        gl.DeleteVertexArray(outlineVao);
        gl.DeleteBuffer(faceVbo);
        gl.DeleteVertexArray(faceVao);
        foreach (var query in _gpuQueries)
            gl.DeleteQuery(query);
        gl.Dispose();
    }
}
