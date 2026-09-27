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

    // The pooled chunk-draw variants of the two world programs: the same GLSL sources compiled
    // a second time with #define CHUNK_DRAW, so their vertex stage reads its per-draw origin and
    // fade alpha out of the ChunkInfo SSBO at uDrawBase + gl_DrawID instead of uModel/uMVP/uAlpha.
    // Every chunk pass runs through these; entities, LOD, drops, TNT and block pops keep the
    // plain programs and SetDrawUniforms.
    private const string ChunkDrawDefines = "#define CHUNK_DRAW";

    /// <summary>SSBO binding of the tile-material table. Binding 0 is ChunkDrawBatch's ChunkInfo.</summary>
    private const int MaterialSsboBinding = 1;

    /// <summary>Texture unit of <see cref="materialAtlas"/>. Units 0-4 are the world shader's
    /// existing samplers and 5 is the rays upsample's transient bind.</summary>
    private const int MaterialUnit = 6;
    private Shader chunkShader;
    private Shader chunkShadowShader;

    /// <summary>The shared vertex/index buffers every chunk's opaque mesh lives in.</summary>
    public ChunkGeometryPool OpaqueChunkGeometry { get; private set; }

    /// <summary>The shared vertex/index buffers every chunk's translucent (water/glass) mesh lives in.</summary>
    public ChunkGeometryPool TranslucentChunkGeometry { get; private set; }

    /// <summary>
    /// The shared vertex/index buffers every LOD region lives in — both a region's terrain shell
    /// AND its sea sheet. One pool covers both because a region's two meshes are born from one
    /// <c>LodMeshData</c> and freed together in one <c>LodRegion.Dispose</c>, so their lifetimes
    /// never diverge; the chunk pools' opaque/translucent split exists for a sizing reason
    /// (512 MB vs 64 MB), not a correctness one, and does not transfer. See
    /// <see cref="ChunkGeometryPool.LodInitialUnits"/> for the sizing derivation.
    /// </summary>
    public ChunkGeometryPool LodGeometry { get; private set; }

    // The persistent-mapped indirect-command ring + ChunkInfo SSBO the chunk passes fill and
    // draw with one MultiDrawElementsIndirect each.
    private ChunkDrawBatch _chunkDraws;
    private UIRenderer ui;
    private BlockCubeCache blockCubes;
    private ItemMeshCache itemMeshes;
    // The quern's spinning plate and handle, built from the top quads of its baked model
    // (see QuernRenderer): the chunk only meshes the base, so this entity completes it.
    private Mesh? _quernTop;

    // The crucible bowl standing in a kiln's opening, built from the crucible item's baked model
    // (see CrucibleItem): it is an item, so nothing meshes it into a chunk.
    private Mesh? _crucibleMesh;

    // One flat liquid quad per molten material, tinted with the material's colour and built on
    // first sight. Both the crucible's pool and a mold's fill draw it, scaled and lifted.
    private readonly Dictionary<byte, Mesh> _moltenQuads = new();
    private uint _playerPaletteTex;
    private Dictionary<string, Mesh> _playerMeshes = new();

    // The slime entity mesh and skin, built from its data-driven Blockbench project at load.
    // Null only when no 'slime' entity kind is registered, in which case the pass is a no-op.
    private Mesh? _slimeMesh;
    private uint _slimeTexture;

    /// <summary>Blockbench project behind the player model; a missing or broken file falls back to built-in Steve.</summary>
    public const string PlayerModelPath = "assets/models/player/player.bbmodel";

    /// <summary>The player shape being drawn: the Blockbench file when it loads, built-in Steve otherwise.</summary>
    public PlayerModel.PlayerRig PlayerRig { get; private set; } = PlayerModel.PlayerRig.BuiltIn();
    private uint textureAtlas;
    /// <summary>Index-parallel to <see cref="textureAtlas"/>: per-tile normal + metallic mask.</summary>
    private uint materialAtlas;
    /// <summary>Per-tile (metallic, roughness, normalStrength) for the world shader, at
    /// <see cref="MaterialSsboBinding"/>. Zero while no tile is reflective.</summary>
    private uint materialSsbo;
    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    private uint faceVao;
    private uint faceVbo;

    // The mining pop's streaming box batch (see PopBoxEmitter): one reusable VAO/VBO with a
    // static index prefix per box, refilled while a block is being mined. Tapered wood tops
    // out at five boxes (column plus four arms), so eight leaves headroom and the buffers
    // grow if a future block ever brings more.
    private const int MaxPopBoxes = 8;
    private uint _popVao;
    private uint _popVbo;
    private uint _popEbo;
    private int _popBoxCapacity = MaxPopBoxes;
    private readonly List<Vertex> _popVerts = new(MaxPopBoxes * 24);
    private readonly List<ushort> _popIndices = new(MaxPopBoxes * 36);

    // The sun disc: one shared unit quad plus its sprite, oriented around _sunDir every frame
    // by RenderSun. _sunVisibility fades it out below the horizon and underwater.
    private uint _sunTexture;
    private Mesh _sunQuad;
    private float _sunVisibility;

    private uint _breakProgressTexture;

    // The cloud layer as one static, stitched mesh (see CloudRenderer), drawn translucent
    // after the water pass. Null until LoadResources builds it.
    private CloudRenderer? _clouds;

    // Underwater sun shafts, one additive full-screen pass. Null until LoadResources builds it.
    private UnderwaterRaysRenderer? _rays;

    // Damage reddening: one full-screen radial-gradient pass, drawn first inside the UI pass.
    // Null until LoadResources builds it.
    private DamageVignetteRenderer? _vignette;

    // Falling leaves: one dynamic billboard batch drawn in the entity pass, after the
    // cubes. Null until LoadResources builds it.
    private LeafParticleRenderer? _leaves;
    private FireParticleRenderer? _fire;
    private BubbleParticleRenderer? _bubbles;

    // Entity healthbars: one five-heart billboard row above every live slime.
    // Null until LoadResources builds it.
    private EntityHealthbarRenderer? _healthbars;

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

    // Temporal reuse: the mirror is re-rendered at most every other frame. These describe the
    // camera that actually produced the current _reflTex, so a frame that skips the render can
    // re-push the matrix that matches the texture instead of this frame's.
    private ReflectionReuse.ReflectionPose _reflPose;
    // A real mirror render happened and _reflTex still matches _reflPose.
    private bool _reflMirrorValid;
    private bool _reflReusedLastFrame;

    // The scene copies the water pass refracts through: the default framebuffer's colour on
    // unit 3 and its depth on unit 4, snapshotted by CopyScene just before the water draws.
    private uint _sceneColorTex;
    private uint _sceneDepthTex;
    private Vector2D<int> _sceneSize;

    // The half-resolution target the underwater sun-shaft march renders into before a
    // full-res depth-aware upsample composites it back (see CreateRaysTarget). Layout is
    // RGBA16F at half the viewport per axis: rgb is the additive shaft colour and a is the
    // world-space distance that pixel marched, capped at
    // WorldGenerationConfig.WaterRayMaxDistance, which the upsample keys its depth-aware
    // weights off. No depth attachment - the march is a full-screen triangle with the depth
    // test off, so there is nothing to test against.
    private uint _raysFbo;
    private uint _raysTex;
    private Vector2D<int> _raysSize;

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

    // Average opaque colour of the leaves tile, uploaded as uLodLeafColor: far LOD crowns
    // sample the cutout leaf texture stretched over whole cells, so their transparent texels
    // fall back to this instead of discarding to sky.
    private Vector3 _lodLeafColor = new(0.17f, 0.29f, 0.07f);
    // 0 at night (and fully underwater), 1 with the sun well up: how much of the direct
    // sun term the shadow lookup is allowed to take away
    private float _shadowStrength;

    // The same value before the underwater fade. Caustics and the sun shafts are exactly the
    // effects a submerged eye is supposed to see, so they (and the shadow map itself) keep
    // using this while the world diffuse fades with _shadowStrength.
    private float _shadowStrengthDry;

    // How high the sun is, 0 at night and 1 well into the day; the ray pass scales with it
    private float _dayness;
    /// <summary>0 at night, 1 in full day; drives the ambience crossfade.</summary>
    public float Dayness => _dayness;
    private Matrix4x4 _lightViewProj = Matrix4x4.Identity;
    // The shadow box centre carried frame to frame by StepShadowCentre; invalid until the first
    // frame with shadows, which seeds it on the camera instead of stepping from a stale point.
    private Vector3 _shadowCentre;
    private bool _shadowCentreValid;

    // Inverse of the current view-projection, for the shaders that reconstruct a world
    // position out of the depth texture (see the depth-convention note in test-fragment.glsl)
    private Matrix4x4 _invViewProj = Matrix4x4.Identity;

    // Underwater tint: the eye state is a hard flag, so it is eased into _underwaterT
    // (0 = above water, 1 = fully submerged) over UnderwaterFadeSeconds
    private static readonly Vector3 UnderwaterFogColor = new(0.05f, 0.22f, 0.42f);
    private static readonly Vector3 UnderwaterAmbientTint = new(0.55f, 0.75f, 1f);
    private static readonly Vector3 LavaFogColor = new(0.55f, 0.12f, 0.02f);
    private static readonly Vector3 LavaAmbientTint = new(1.2f, 0.6f, 0.4f);
    private double _prevTime;
    private float _underwaterT;
    private bool _eyeInLava;
    // Eases the lava-only fades (sky clear colour, sun disc) exactly as _underwaterT eases the
    // water ones: _eyeInLava is a hard flag that drops the frame the eye leaves lava while
    // _underwaterT is still fading out, which would otherwise pop the sky back in one frame.
    private float _lavaT;

    // Per-pass GPU time: every render pass gets its own TIME_ELAPSED query object per
    // ring slot (see BeginGpuPass). GL forbids nesting TIME_ELAPSED, but consecutive
    // non-overlapping ranges are legal, so the passes are bracketed one after another and
    // each range lands in its own query. A result is only ever read when its slot comes
    // back around, a few frames late, so this never stalls. Three slots cover the usual
    // one-to-two frame latency. EntityPass runs in two segments per frame (entities, then
    // bubbles after the water pass), hence up to MaxGpuSegments ranges per (pass, slot).
    private const int GpuRingSize = 3;
    private const int MaxGpuSegments = 2;
    // Every section, not just the render passes: UiPass is no longer the last enum value, and an
    // out-of-range index inside BeginGpuPass would fail silently, so size by the enum.
    private static readonly int GpuSectionCount = Enum.GetValues<ProfileSection>().Length;
    private readonly uint[,,] _gpuPassQueries = new uint[GpuSectionCount, GpuRingSize, MaxGpuSegments];
    private readonly bool[,,] _gpuPassPending = new bool[GpuSectionCount, GpuRingSize, MaxGpuSegments];
    private readonly int[] _gpuPassSegUsed = new int[GpuSectionCount];
    private int _gpuSlot;

    // R6's gate. _gpuTimingOn is latched once per frame in BeginGpuFrame and held for the whole
    // frame (see the comment there); it starts true so a frame that somehow ran before the first
    // BeginGpuFrame behaves exactly as it did before the gate existed. The three counters are the
    // gate's instrument: Dispose logs them once, which is the only place the gated configuration
    // can report itself at all (Program.LogDebugLine returns early when debug output is off, and
    // that is the same flag the gate reads).
    private bool _gpuTimingOn = true;
    private long _gpuFrameIndex;
    private long _gpuTimedFrames;
    private long _gpuQueryCalls;

    /// <summary>Chunks the last <see cref="RenderWorld"/> call passed the frustum test.</summary>
    public int LastVisibleChunkCount { get; private set; }

    private readonly Vector3[] _pointPos = new Vector3[WorldGenerationConfig.MaxPointLights];
    private readonly Vector3[] _pointColors = new Vector3[WorldGenerationConfig.MaxPointLights];
    private int _pointCount;

    // World-shader uniform locations, resolved once in LoadResources. The first four are the
    // only uniforms a world draw still touches, so the per-draw cost is three (four for TNT,
    // five for a flashing slime) glUniform calls with no dictionary lookup in between; the arrays feed ApplyFrameUniforms.
    private int _locModel;
    private int _locMvp;
    private int _locAlpha;
    private int _locFlash;
    private int _locHitFlash;
    private int _locAlphaCutoff;
    private int[] _locPointPos = [];
    private int[] _locPointColor = [];

    // The same, on the CHUNK_DRAW variants. uModel/uMVP/uAlpha do not exist there (the vertex
    // stage takes them from the SSBO), so only the view-projection, the SSBO base index and the
    // handful of frame uniforms the chunk programs still read are resolved.
    private int _locChunkViewProj;
    private int _locChunkDrawBase;
    private int _locChunkAlphaCutoff;
    private int _locChunkShadowViewProj;
    private int _locChunkShadowDrawBase;
    private int _locChunkShadowTime;
    private int _locChunkShadowSway;
    private int[] _locChunkPointPos = [];
    private int[] _locChunkPointColor = [];

    public RenderManager(IWindow window)
    {
        gl = window.CreateOpenGL();
        gl.Enable(GLEnum.DepthTest);
        gl.DepthFunc(GLEnum.Lequal);
        _viewportSize = window.Size;

        // Which GPU actually ran matters for every timing: a hybrid laptop happily renders
        // this on the iGPU unless told otherwise, and the numbers then mean nothing.
        Log.WriteLine($"GPU: {gl.GetStringS(StringName.Renderer)} | {gl.GetStringS(StringName.Vendor)} | GL {gl.GetStringS(StringName.Version)}");

        for (var s = 0; s < GpuSectionCount; s++)
        for (var r = 0; r < GpuRingSize; r++)
        for (var g = 0; g < MaxGpuSegments; g++)
            _gpuPassQueries[s, r, g] = gl.GenQuery();

        LoadResources();
    }

    /// <summary>
    /// Prepares this frame's GPU timers: drains whatever per-pass results sit in the slot
    /// about to be reused and resets the per-pass segment counters. A query is only read
    /// when the driver says it is available, so this never stalls the pipeline; a result
    /// still missing after a full lap of the ring is simply dropped. Starts no query
    /// itself — the passes bracket their own ranges with
    /// <see cref="BeginGpuPass"/>/<see cref="EndGpuPass"/> further down this frame.
    /// <para>
    /// Also latches this frame's timing decision (<see cref="GpuTimingGate.ShouldTime"/>) into a
    /// field that is held for the whole frame, so every <see cref="BeginGpuPass"/> and
    /// <see cref="EndGpuPass"/> below reads one stable answer. While the overlay is down only one
    /// frame in <see cref="WorldGenerationConfig.GpuTimingIdleInterval"/> issues queries; the drain
    /// below runs regardless.
    /// </para>
    /// </summary>
    /// <param name="overlayOn">The <c>X</c> profiler overlay is up this frame.</param>
    public void BeginGpuFrame(bool overlayOn)
    {
        // Latched once, here, and never re-read mid-frame: EndGpuPass advances _gpuPassSegUsed, so a
        // flip landing between a BeginGpuPass and its EndGpuPass would open a TIME_ELAPSED range that
        // is never closed and desync every later pass's segment index for the rest of the frame.
        _gpuTimingOn = GpuTimingGate.ShouldTime(overlayOn, _gpuFrameIndex);
        _gpuFrameIndex++;
        if (_gpuTimingOn) _gpuTimedFrames++;

        // The drain stays UNCONDITIONAL whatever the gate says, and that is load-bearing: a
        // _gpuPassPending entry written while the gate was open must still be cleared after the gate
        // closes, or that slot is reused with a stale pending flag forever. It also means a
        // gate-closed frame is not necessarily query-free — with GpuRingSize == 3 the queries issued on
        // a timed frame k are drained on frame k+3, which is itself a gate-closed frame.
        for (var s = 0; s < GpuSectionCount; s++)
        for (var g = 0; g < MaxGpuSegments; g++)
        {
            if (!_gpuPassPending[s, _gpuSlot, g]) continue;
            _gpuPassPending[s, _gpuSlot, g] = false;

            gl.GetQueryObject(_gpuPassQueries[s, _gpuSlot, g],
                QueryObjectParameterName.ResultAvailable, out int available);
            _gpuQueryCalls++;
            if (available == 0) continue;

            gl.GetQueryObject(_gpuPassQueries[s, _gpuSlot, g],
                QueryObjectParameterName.Result, out long nanoseconds);
            _gpuQueryCalls++;
            FrameProfiler.ReportGpuPass((ProfileSection)s, nanoseconds);
        }

        Array.Clear(_gpuPassSegUsed);
    }

    /// <summary>
    /// Opens one pass's GPU timer. Ranges must be consecutive and non-overlapping: every
    /// <see cref="BeginGpuPass"/> needs its <see cref="EndGpuPass"/> before the next pass
    /// begins, since GL forbids nesting TIME_ELAPSED. A pass with several draw blocks per
    /// frame (EntityPass) simply brackets each block; each lands in its own query object
    /// and the samples add up in the overlay average.
    /// <para>
    /// No-ops on a frame the gate closed. That decision is latched once per frame in
    /// <see cref="BeginGpuFrame"/> and held for the whole frame, never re-read here: were it read
    /// per call, a toggle landing between this and its <see cref="EndGpuPass"/> would open a
    /// TIME_ELAPSED range nothing ever closes and desync every later pass's segment index.
    /// </para>
    /// </summary>
    public void BeginGpuPass(ProfileSection section)
    {
        if (!_gpuTimingOn) return;
        var s = (int)section;
        var seg = _gpuPassSegUsed[s];
        if (seg >= MaxGpuSegments) return;
        gl.BeginQuery(QueryTarget.TimeElapsed, _gpuPassQueries[s, _gpuSlot, seg]);
        _gpuQueryCalls++;
        _gpuPassPending[s, _gpuSlot, seg] = true;
    }

    /// <summary>
    /// Closes the range opened by <see cref="BeginGpuPass"/>. Reads the same per-frame latch, for
    /// the same reason: the gate is decided once in <see cref="BeginGpuFrame"/> and held, so a
    /// bracket that opened this frame is always the bracket that closes it.
    /// </summary>
    public void EndGpuPass(ProfileSection section)
    {
        if (!_gpuTimingOn) return;
        var s = (int)section;
        if (_gpuPassSegUsed[s] >= MaxGpuSegments) return;
        gl.EndQuery(QueryTarget.TimeElapsed);
        _gpuQueryCalls++;
        _gpuPassSegUsed[s]++;
    }

    /// <summary>Moves the query ring on; the drained slot is reused two frames later.</summary>
    public void EndGpuFrame()
    {
        _gpuSlot = (_gpuSlot + 1) % GpuRingSize;
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
        _lodLeafColor = LoadOpaqueAverage("assets/textures/blocks/leaves.png", _lodLeafColor);
        ContentRegistry.BakeModels();
        // After the bake, the first point that knows which tiles cutout blocks wear: their
        // box-filtered mips would average leaf alpha under the cutoff and thin canopies out.
        TextureAtlas.ApplyCutoutMips(gl, textureAtlas, BlockType.CutoutTiles(), WorldGenerationConfig.AlphaCutoff);
        // After the bake, because the material table is keyed by the tiles the bake resolves.
        materialAtlas = TextureAtlas.BuildMaterial(gl, "assets/textures/blocks_material");
        CreateMaterialBuffer();

        worldShader = new Shader(gl, vs, fs);
        // The same two sources again, with the chunk-draw variant switched on: everything a
        // chunk pass draws goes through this program (see ChunkDrawDefines).
        chunkShader = new Shader(gl, vs, fs, ChunkDrawDefines);
        InitWorldProgram(worldShader);
        InitWorldProgram(chunkShader);
        // uFlash is only ever non-zero for a primed-TNT draw, which never goes through the
        // chunk program; the cutout default matches BeginFrame's. Both are set once because
        // nothing else on this program ever writes them.
        chunkShader.SetUniform("uFlash", 0f);
        chunkShader.SetUniform("uHitFlash", 0f);
        chunkShader.SetUniform("uAlphaCutoff", WorldGenerationConfig.AlphaCutoff);
        CreateReflectionTarget();
        CreateSceneCopy();
        CreateRaysTarget();

        // Everything a world draw sets per draw, resolved once here (see SetDrawUniforms)
        _locModel = worldShader.GetUniformLocation("uModel");
        _locMvp = worldShader.GetUniformLocation("uMVP");
        _locAlpha = worldShader.GetUniformLocation("uAlpha");
        _locFlash = worldShader.GetUniformLocation("uFlash");
        _locHitFlash = worldShader.GetUniformLocation("uHitFlash");
        _locAlphaCutoff = worldShader.GetUniformLocation("uAlphaCutoff");
        _locPointPos = worldShader.GetUniformArrayLocations("uPointPos", WorldGenerationConfig.MaxPointLights);
        _locPointColor = worldShader.GetUniformArrayLocations("uPointColor", WorldGenerationConfig.MaxPointLights);

        _locChunkViewProj = chunkShader.GetUniformLocation("uViewProj");
        _locChunkDrawBase = chunkShader.GetUniformLocation("uDrawBase");
        _locChunkAlphaCutoff = chunkShader.GetUniformLocation("uAlphaCutoff");
        _locChunkPointPos = chunkShader.GetUniformArrayLocations("uPointPos", WorldGenerationConfig.MaxPointLights);
        _locChunkPointColor = chunkShader.GetUniformArrayLocations("uPointColor", WorldGenerationConfig.MaxPointLights);

        selectionShader = new Shader(gl, selectionVs, selectionFs);
        breakShader = new Shader(gl, breakVs, breakFs);
        // The crack sprite is a single column of BreakOverlayFrames stacked 32x32 frames, so
        // each frame becomes its own array slice and no mip level bleeds a neighbour in.
        _breakProgressTexture = TextureLoader.LoadSpritesheet(gl, "assets/textures/environment/break_progress.png",
            1, WorldGenerationConfig.BreakOverlayFrames, WorldGenerationConfig.BreakOverlayFrames);
        breakShader.Use();
        breakShader.SetUniform("uBreakProgress", 0);
        breakShader.SetUniform("uOpacity", WorldGenerationConfig.BreakOverlayOpacity);

        shadowShader = new Shader(gl, shadowVs, shadowFs);
        // The depth pass still samples the atlas, to punch the cutout tiles' holes
        shadowShader.Use();
        shadowShader.SetUniform("uTextureAtlas", 0);

        chunkShadowShader = new Shader(gl, shadowVs, shadowFs, ChunkDrawDefines);
        chunkShadowShader.Use();
        chunkShadowShader.SetUniform("uTextureAtlas", 0);
        _locChunkShadowViewProj = chunkShadowShader.GetUniformLocation("uViewProj");
        _locChunkShadowDrawBase = chunkShadowShader.GetUniformLocation("uDrawBase");
        _locChunkShadowTime = chunkShadowShader.GetUniformLocation("uTime");
        _locChunkShadowSway = chunkShadowShader.GetUniformLocation("uSwayStrength");
        CreateShadowMap();

        // The shared chunk and LOD geometry, and the ring the chunk passes build their draw
        // commands in. All four issue GL calls, so they belong here on the render thread; World
        // (and through it ChunkManager and Chunk.UpdateMesh, and LodManager) is handed the three
        // pools by Program.
        OpaqueChunkGeometry = new ChunkGeometryPool(gl, ChunkGeometryPool.OpaqueInitialUnits, "opaque");
        TranslucentChunkGeometry = new ChunkGeometryPool(gl, ChunkGeometryPool.TranslucentInitialUnits, "translucent");
        LodGeometry = new ChunkGeometryPool(gl, ChunkGeometryPool.LodInitialUnits, "lod");
        _chunkDraws = new ChunkDrawBatch(gl);

        sunShader = new Shader(gl, sunVs, sunFs);
        sunShader.Use();
        sunShader.SetUniform("uSunTexture", 0);

        _sunTexture = TextureLoader.LoadFromFile(gl, "assets/textures/environment/sun.png");
        // The loader hard-codes Nearest for the block tiles; the 64px disc is scaled up a lot
        // on screen, so it needs Linear or its edge turns into a staircase.
        gl.BindTexture(TextureTarget.Texture2DArray, _sunTexture);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        _sunQuad = Primitives.CreateSquare(gl, 1f);
        _clouds = new CloudRenderer(gl);
        _rays = new UnderwaterRaysRenderer(gl);
        _vignette = new DamageVignetteRenderer(gl);
        _leaves = new LeafParticleRenderer(gl);
        _fire = new FireParticleRenderer(gl);
        _bubbles = new BubbleParticleRenderer(gl);
        _healthbars = new EntityHealthbarRenderer(gl);

        blockCubes = new BlockCubeCache(gl);
        itemMeshes = new ItemMeshCache(gl);
        // Item textures are uploaded lazily, on first sight, from inside the entity and UI passes,
        // and TextureLoader.Upload binds on whatever unit is live. UIRenderer shares this instance,
        // so one hook covers both callers.
        itemMeshes.GlStateChanged = InvalidateTextureUnits;
        _quernTop = BuildQuernTopMesh();
        _crucibleMesh = BuildCrucibleMesh();
        try
        {
            PlayerRig = PlayerModel.PlayerRig.FromBbmodel(BbmodelLoader.Load(PlayerModelPath));
        }
        catch (Exception e)
        {
            Log.WriteLine($"Player model '{PlayerModelPath}' not used ({e.Message}); using built-in Steve.");
        }
        _playerPaletteTex = TextureLoader.Upload(gl, PlayerRig.TexturePixels,
            PlayerRig.TextureWidth, PlayerRig.TextureHeight);
        _playerMeshes = PlayerModel.BuildMeshes(gl, PlayerRig);
        if (EntityRegistry.TryGet("slime") is { } slimeType)
        {
            var slimeModel = EntityModel.Load(slimeType.ModelPath);
            _slimeMesh = slimeModel.BuildMesh(gl);
            _slimeTexture = TextureLoader.Upload(gl, slimeModel.TexturePixels,
                slimeModel.TextureWidth, slimeModel.TextureHeight);
        }
        ui = new UIRenderer(gl, textureAtlas, blockCubes, itemMeshes);
        CreateSelectionGeometry();
        CreatePopGeometry();
        // Everything above binds raw - the atlas build, the loader, the sun's filter tweak and
        // the three render targets - so nothing the cache thinks it knows survives load.
        InvalidateTextureUnits();
    }

    /// <summary>
    /// Uploads the per-tile material table (see <see cref="TileMaterials"/>) as an immutable SSBO
    /// the world fragment shader indexes by its existing <c>fLayer</c>. Nothing is created when no
    /// tile is reflective, which leaves <c>uMaterialCount</c> at 0 and switches the path off.
    /// </summary>
    private unsafe void CreateMaterialBuffer()
    {
        if (!TileMaterials.AnyReflective) return;

        var data = TileMaterials.ToBufferData();
        materialSsbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, materialSsbo);
        fixed (float* ptr = data)
        {
            gl.BufferStorage(BufferStorageTarget.ShaderStorageBuffer,
                (nuint)(data.Length * sizeof(float)), ptr, BufferStorageMask.None);
        }
        // The table never changes after load, so one bind here holds for every pass and both
        // world programs; nothing else in the frame touches this binding point.
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, MaterialSsboBinding, materialSsbo);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    /// <summary>
    /// The world-program state that is settled once at load: the five sampler units, the sea
    /// surface, the initial reflection validity and the water model's tuning constants. Both
    /// world programs (the plain one and the CHUNK_DRAW variant) need every one of them,
    /// because uniform values live in the program object, not in the GL context.
    /// </summary>
    private void InitWorldProgram(Shader program)
    {
        // SetUniform writes to the currently bound program, so bind first — otherwise
        // the sampler unit silently stays at its default.
        program.Use();
        program.SetUniform("uTextureAtlas", 0);
        program.SetUniform("uShadowMap", 1);
        program.SetUniform("uReflectionMap", 2);
        program.SetUniform("uSceneColor", 3);
        program.SetUniform("uSceneDepth", 4);
        program.SetUniform("uMaterialAtlas", MaterialUnit);
        // Doubles as the switch for the whole reflective path: left at 0 the shader never reads
        // the material buffer, which is what keeps the ItemDebugger (same shaders, no buffer)
        // and a world with no reflective tiles safe.
        program.SetUniform("uMaterialCount", materialSsbo != 0 ? TileMaterials.ByLayer.Length : 0);
        program.SetUniform("uWaterPlaneY", WaterPlaneY);
        program.SetUniform("uReflValid", 0f);
        // The water model's tuning constants never change at runtime, so they go up once here
        // instead of riding ApplyFrameUniforms every frame.
        program.SetUniform("uWaterNormalScale", WorldGenerationConfig.WaterNormalScale);
        program.SetUniform("uWaterRefractStrength", WorldGenerationConfig.WaterRefractStrength);
        program.SetUniform("uWaterWindowRefract", WorldGenerationConfig.WaterWindowRefractScale);
        program.SetUniform("uWaterAbsorb", WorldGenerationConfig.WaterAbsorb);
        program.SetUniform("uWaterScatter", WorldGenerationConfig.WaterScatter);
        program.SetUniform("uWaterCausticStrength", WorldGenerationConfig.WaterCausticStrength);
        program.SetUniform("uWaterFancyNear", WorldGenerationConfig.WaterFancyNearBlocks);
        program.SetUniform("uWaterFancyFar", WorldGenerationConfig.WaterFancyFarBlocks);
        program.SetUniform("uLavaLayer", (float)ContentRegistry.RequireBlock("lava").Top);
        program.SetUniform("uLodLeafColor", _lodLeafColor);
    }

    /// <summary>
    /// Averages a tile's opaque texels to 0..1, falling back to
    /// <paramref name="fallback"/> when none qualify.
    /// </summary>
    private static Vector3 LoadOpaqueAverage(string path, Vector3 fallback)
    {
        using var stream = System.IO.File.OpenRead(path);
        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        long r = 0, g = 0, b = 0;
        var n = 0;
        for (var i = 0; i + 3 < image.Data.Length; i += 4)
        {
            if (image.Data[i + 3] < 128) continue;
            r += image.Data[i];
            g += image.Data[i + 1];
            b += image.Data[i + 2];
            n++;
        }
        return n == 0 ? fallback : new Vector3(r / (n * 255f), g / (n * 255f), b / (n * 255f));
    }

    /// <summary>
    /// Sets a uniform that has to hold on both world programs (the plain one and the CHUNK_DRAW
    /// variant) but is not part of <see cref="ApplyFrameUniforms"/> — the reflection state, which
    /// <see cref="RenderReflection"/> writes at three points in the frame. Water is drawn through
    /// the chunk program, so a value pushed to only one of the two shows up as a missing
    /// reflection. Leaves <c>worldShader</c> bound, like ApplyFrameUniforms.
    /// </summary>
    private void SetWorldUniform(string name, float value)
    {
        chunkShader.Use();
        chunkShader.SetUniform(name, value);
        worldShader.Use();
        worldShader.SetUniform(name, value);
    }

    /// <inheritdoc cref="SetWorldUniform(string, float)"/>
    private void SetWorldUniform(string name, in Matrix4x4 value)
    {
        chunkShader.Use();
        chunkShader.SetUniform(name, value);
        worldShader.Use();
        worldShader.SetUniform(name, value);
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
        // Raw binds above, and _shadowDepthTex is a brand-new name.
        InvalidateTextureUnits();
    }

    public void SetViewport(Vector2D<int> size)
    {
        _viewportSize = size;
        gl.Viewport(size);
        CreateReflectionTarget();
        CreateSceneCopy();
        CreateRaysTarget();
        // Belt-and-braces: each creator already invalidates on its own recreate path, but a
        // resize is rare enough that a second reset costs nothing and cannot be forgotten.
        InvalidateTextureUnits();
    }

    /// <summary>
    /// Where a fire cell's warm point light sits inside it: a block above the cell origin, so a
    /// campfire lights what stands around it rather than only its own pile. Deliberately higher
    /// than <see cref="FireParticleSystem.SpawnOffset"/>, which is where the flame itself is —
    /// a caller passing an off-cell flame (the kiln) gets its light that same 0.65 higher.
    /// </summary>
    public static readonly Vector3 FireLightOffset = new(0.5f, 1.0f, 0.5f);

    /// <summary>
    /// Advances the day/night cycle and pushes the resulting sun, sky and point-light
    /// state into the per-frame fields. Called once per frame before <see cref="BeginFrame"/>,
    /// which uploads the result to the world shader through <see cref="ApplyFrameUniforms"/>.
    /// <paramref name="underwater"/> is whether the camera's eye sits below a fluid surface;
    /// it fades the underwater tint in and out. <paramref name="eyeInLava"/> picks the lava
    /// variant of that tint, so a submerged view reads red instead of blue.
    /// </summary>
    public void SetLighting(double time, Camera? camera, IReadOnlyList<PrimedTnt>? primed, bool underwater, bool eyeInLava,
        IReadOnlyList<Vector3>? campfires = null)
    {
        // The window clock is the only time source this pass has; clamp the step so a hitch
        // (or the very first frame) cannot jump the fade
        var dt = (float)MathUtils.Clamp(time - _prevTime, 0.0, 0.25);
        _prevTime = time;

        _time = (float)time;
        if (camera != null)
        {
            _cameraPos = camera.RenderEye;
            // Inverting the exact matrix the depth was written with is what makes the shaders'
            // position reconstruction independent of the clip-depth convention.
            if (!Matrix4x4.Invert(camera.GetViewProjection(), out _invViewProj))
                _invViewProj = Matrix4x4.Identity;
        }

        // The sun orbits once per DayLengthSeconds; the phase starts it mid-morning
        var angle = (float)(time / WorldGenerationConfig.DayLengthSeconds) * MathF.Tau + 0.8f;
        _sunDir = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        var elevation = _sunDir.Y;

        // The shadow camera fades itself out as the sun sets: at a grazing elevation the
        // shadows stretch to the horizon and the ortho box cannot hold them, and at night
        // there is no direct sun left to remove anyway.
        _shadowStrength = GameSettings.Shadows
            ? Smooth01((elevation - 0.02f) / 0.12f)
            : 0f;

        // The disc itself fades over a much narrower band than the light does, so it sinks
        // out of sight shortly after the horizon instead of hanging in the night sky
        _sunVisibility = Smooth01((elevation + 0.08f) / 0.14f);

        var dayness = Smooth01((elevation + 0.08f) / 0.33f);
        _dayness = dayness;
        var duskAmount = MathUtils.Clamp01(1f - MathF.Abs(elevation) * 5f);

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
        // Lit campfires each add a warm flickering point light above the pile
        if (campfires != null)
        {
            var flicker = 0.9f + 0.25f * MathF.Sin(_time * 11f) * MathF.Sin(_time * 5.3f + 1.7f);
            foreach (var cell in campfires)
            {
                if (_pointCount >= WorldGenerationConfig.MaxPointLights) break;
                _pointPos[_pointCount] = cell + FireLightOffset;
                _pointColors[_pointCount] = new Vector3(1f, 0.5f, 0.15f) * (1.6f * flicker);
                _pointCount++;
            }
        }
        for (var i = _pointCount; i < WorldGenerationConfig.MaxPointLights; i++)
        {
            _pointPos[i] = Vector3.Zero;
            _pointColors[i] = Vector3.Zero;
        }

        // Underwater the fog closes in to a few blocks and goes blue-green and the light dims
        // and cools, so a submerged view reads as being inside the water rather than looking
        // at a blue filter. Scaled by dayness so night water stays dark. The clear colour and
        // the sun disc stay dry in water: they are what the surface's Snell window shows from
        // below, so only the fog closes in. Lava is an opaque glow, so it still fades both to
        // red (eased through _lavaT) and stays bright at night.
        _eyeInLava = eyeInLava && underwater;
        _underwaterT = MathUtils.Clamp01(
            _underwaterT + (underwater ? dt : -dt) / WorldGenerationConfig.UnderwaterFadeSeconds);
        var wet = MathUtils.SmoothStep(_underwaterT);
        _lavaT = MathUtils.Clamp01(_lavaT + (_eyeInLava ? dt : -dt) / WorldGenerationConfig.UnderwaterFadeSeconds);
        var lavaWet = wet * MathUtils.SmoothStep(_lavaT);

        // Underwater the sun is a diffuse glow, so the crisp shadow edges go with the tint.
        // This has to run before the early return below, or a submerged view would keep them.
        // _shadowStrengthDry keeps the unfaded value: the caustics and the underwater sun
        // shafts are exactly what a submerged eye should see, and both need the shadow map
        // to still be rendered and sampled while the world diffuse fades.
        _shadowStrengthDry = _shadowStrength;
        _shadowStrength *= 1f - wet;
        // The disc stays in water: the surface's Snell window shows it refracted from below
        // (the scene copy captures it). Only lava, an opaque glow, still hides it.
        _sunVisibility *= 1f - lavaWet;
        UpdateShadowCamera(camera);

        _fogNear = MathUtils.Lerp(WorldGenerationConfig.FogNearBlocks,
            WorldGenerationConfig.UnderwaterFogNearBlocks, wet);
        _fogFar = MathUtils.Lerp(WorldGenerationConfig.FogFarBlocks,
            WorldGenerationConfig.UnderwaterFogFarBlocks, wet);

        if (wet <= 0f) return;

        var underwaterColor = _eyeInLava
            ? LavaFogColor * (0.6f + 0.4f * dayness)
            : UnderwaterFogColor * (0.15f + 0.85f * dayness);
        _fogColor = Mix(_fogColor, underwaterColor, wet);
        // _fogColor was copied from the dry _skyColor earlier, so this leaves the fog tint alone;
        // afterwards _skyColor only feeds the clear colour, which the window shows from below.
        _skyColor = Mix(_skyColor, underwaterColor, lavaWet);
        _ambient = Mix(_ambient, _ambient * (_eyeInLava ? LavaAmbientTint : UnderwaterAmbientTint), wet);
        _sunIntensity = MathUtils.Lerp(_sunIntensity, _sunIntensity * 0.5f, wet);
    }

    /// <summary>
    /// Builds the light-space matrix for this frame: an orthographic box of half-extent
    /// <see cref="WorldGenerationConfig.ShadowRadiusBlocks"/> centred a little ahead of the
    /// camera (most of what the player looks at is in front of them) and looking down the sun
    /// direction. <see cref="StepShadowCentre"/> carries that centre forward in whole shadow
    /// texels, or the sub-texel drift as the player walks makes every shadow edge crawl.
    /// </summary>
    private void UpdateShadowCamera(Camera? camera)
    {
        if (camera == null || _shadowStrengthDry <= 0f) return;

        const float r = WorldGenerationConfig.ShadowRadiusBlocks;
        const float d = 2.5f * r;
        var target = camera.RenderEye + camera.ViewDirection * (r * 0.35f);
        _shadowCentre = _shadowCentreValid
            ? StepShadowCentre(_shadowCentre, target, _sunDir, r, WorldGenerationConfig.ShadowMapSize)
            : target;
        _shadowCentreValid = true;

        var lightView = Matrix4x4.CreateLookAt(_shadowCentre + _sunDir * d, _shadowCentre, Vector3.UnitY);
        // The sun's 0.35 Z tilt keeps it more than 19 degrees off vertical, so UnitY is
        // never parallel to the view direction and the look-at stays well defined
        var lightProj = Matrix4x4.CreateOrthographic(2f * r, 2f * r, d - 2f * r, d + 2f * r);
        _lightViewProj = lightView * lightProj;
    }

    /// <summary>
    /// Advances a shadow box centre towards the point this frame wants to centre on, so the light
    /// view can be rebuilt around it. The centre is a running value rather than a fresh function of
    /// the desired point, and that is the whole point: it follows the desired point in whole
    /// shadow-texel steps along the light's lateral axes and tracks its depth exactly.
    /// <para>
    /// Quantizing the movement (not the position) is what keeps the map still. Whole-texel lateral
    /// steps are invisible - geometry and lookup shift by the same integer count of texels - while
    /// anything with a fractional texel in it resamples the map and shimmers. Deriving the snapped
    /// centre from scratch each frame instead compares the point against a lattice that rotates
    /// with the sun, so the "nearest" lattice vertex hops by a fractional texel every frame and
    /// every shadow edge twitches at the frame rate; the further the camera sits from the world
    /// origin, the faster the lattice sweeps past it and the worse it gets. Measuring the move from
    /// the previous centre sidesteps that entirely: a camera that has not moved asks for a zero
    /// move, so the projection is identical and nothing twitches, however the sun turns.
    /// </para>
    /// </summary>
    internal static Vector3 StepShadowCentre(Vector3 previous, Vector3 desired, Vector3 sunDir,
        float radiusBlocks, int mapSize)
    {
        var texel = 2f * radiusBlocks / mapSize;
        var delta = desired - previous;
        var along = Vector3.Dot(delta, sunDir);
        var lateral = delta - sunDir * along;
        // Columns, not rows: Vector3.Transform treats a matrix as row-vector form, so the camera's
        // basis vectors in world space are the columns of the view rotation.
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sunDir, Vector3.UnitY);
        var right = new Vector3(rotation.M11, rotation.M21, rotation.M31);
        var up = new Vector3(rotation.M12, rotation.M22, rotation.M32);
        var stepsRight = MathF.Round(Vector3.Dot(lateral, right) / texel);
        var stepsUp = MathF.Round(Vector3.Dot(lateral, up) / texel);
        return previous + sunDir * along + right * (stepsRight * texel) + up * (stepsUp * texel);
    }

    /// <summary>
    /// Depth-only pass that fills the shadow map with every caster inside the light's ortho
    /// box: opaque chunk meshes, primed TNT and toppling trees. Runs first each frame, before
    /// anything draws to the default framebuffer; water never casts. Culling uses the light
    /// frustum, so the caster set is exactly what the box can hold.
    /// <paramref name="chunks"/> is <see cref="ChunkManager.Renderable"/>, a superset of the
    /// casters: water-only chunks are in it and are skipped here. The chunk casters that
    /// survive the culls are drawn with a single <c>glMultiDrawElementsIndirect</c> through
    /// <see cref="ChunkDrawBatch"/>; TNT, falling blocks and trees keep the plain program.
    /// </summary>
    public void RenderShadows(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<PrimedTnt> primed, IReadOnlyList<FallingBlock> falling, IEnumerable<TopplingTree> trees,
        PlayerModel? player = null, IReadOnlyList<Slime>? slimes = null)
    {
        // The dry strength, not the wet-faded one: submerged, the caustics and the sun shafts
        // still sample this map even though the world diffuse has stopped using it.
        if (camera == null || _shadowStrengthDry <= 0f) return;

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
        BindUnit(1, TextureTarget.Texture2D, 0);
        BindUnit(0, TextureTarget.Texture2DArray, textureAtlas);
        shadowShader.SetUniform("uTime", _time);
        shadowShader.SetUniform("uSwayStrength", GameSettings.LeafSway);

        var frustum = new Frustum(_lightViewProj);

        // The chunk casters go through the CHUNK_DRAW variant and one MultiDrawElementsIndirect;
        // the entity loops below keep the plain program, which is still bound above for them.
        chunkShadowShader.Use();
        chunkShadowShader.SetUniform(_locChunkShadowTime, _time);
        chunkShadowShader.SetUniform(_locChunkShadowSway, GameSettings.LeafSway);
        chunkShadowShader.SetUniform(_locChunkShadowViewProj, _lightViewProj);
        _chunkDraws.BeginPass(chunks.Count);

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

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax)) continue;

            _chunkDraws.Add(chunk.OpaqueSlice!.Value, chunk.WorldMin, 1f);
        }

        _chunkDraws.Draw(OpaqueChunkGeometry.Vao, chunkShadowShader, _locChunkShadowDrawBase);
        shadowShader.Use();

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

        if (slimes != null && _slimeMesh != null)
        {
            foreach (var slime in slimes)
            {
                var model = SlimeTransform(slime);
                shadowShader.SetUniform("uModel", model);
                shadowShader.SetUniform("uLightMVP", model * _lightViewProj);
                _slimeMesh.Render();
            }
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

        if (player != null && camera != null)
        {
            BindUnit(0, TextureTarget.Texture2DArray, _playerPaletteTex);
            foreach (var part in PlayerModel.BodyParts)
                DrawPlayerPartShadow(player, part, camera);
            DrawPlayerPartShadow(player, "Head", camera);
            BindUnit(0, TextureTarget.Texture2DArray, textureAtlas);
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
    /// Returns true when the target was actually (re)created, which also invalidates the
    /// temporal-reuse cache in <see cref="RenderReflection"/> - the fresh colour texture holds
    /// nothing a water fragment may sample.
    /// </summary>
    private bool CreateReflectionTarget()
    {
        var scale = GameSettings.ReflectionScales[
            Math.Clamp(GameSettings.ReflectionIndex, 0, GameSettings.ReflectionScales.Length - 1)];
        var w = Math.Max(2, (int)(_viewportSize.X * scale));
        var h = Math.Max(2, (int)(_viewportSize.Y * scale));
        if (_reflFbo != 0 && _reflSize.X == w && _reflSize.Y == h) return false;

        DeleteReflectionTarget();
        // The new colour texture is TexImage2D'd with null data below, so its contents are
        // undefined: the cached mirror is gone and the next eligible frame must render one.
        // Clearing the flag here (rather than relying on the return value) is what covers the
        // SetViewport call site, whose recreation happens outside RenderReflection entirely.
        _reflMirrorValid = false;
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
        // Recreate path only. The size-unchanged early return above binds nothing, and this
        // method runs every frame - invalidating up there would cost a full rebind per frame.
        InvalidateTextureUnits();
        return true;
    }

    private void DeleteReflectionTarget()
    {
        if (_reflFbo == 0) return;
        gl.DeleteFramebuffer(_reflFbo);
        gl.DeleteTexture(_reflTex);
        gl.DeleteTexture(_reflDepthTex);
        _reflFbo = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        InvalidateTextureUnits();
    }

    /// <summary>
    /// Allocates the two full-resolution textures <see cref="CopyScene"/> snapshots the default
    /// framebuffer into. No FBO and no depth attachment: these are copy destinations only, read
    /// back by the water fragment shader on units 3 and 4. Called from
    /// <see cref="LoadResources"/> and <see cref="SetViewport"/>, recreating only on a resize.
    /// </summary>
    private void CreateSceneCopy()
    {
        var w = Math.Max(2, _viewportSize.X);
        var h = Math.Max(2, _viewportSize.Y);
        if (_sceneColorTex != 0 && _sceneSize.X == w && _sceneSize.Y == h) return;

        DeleteSceneCopy();
        _sceneSize = new Vector2D<int>(w, h);

        _sceneColorTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        // Linear: the refraction offset lands between texels, and a nearest fetch there
        // turns the ripple into a staircase.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _sceneDepthTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _sceneDepthTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }
        // Nearest, and deliberately no compare mode: unlike the shadow map this one is read
        // as a plain sampler2D for its raw depth value, not as a hardware depth test.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        // Recreate path only (the size-unchanged early return above binds nothing).
        InvalidateTextureUnits();
    }

    private void DeleteSceneCopy()
    {
        if (_sceneColorTex == 0) return;
        gl.DeleteTexture(_sceneColorTex);
        gl.DeleteTexture(_sceneDepthTex);
        _sceneColorTex = 0;
        _sceneDepthTex = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        InvalidateTextureUnits();
    }

    /// <summary>
    /// Builds the half-resolution colour target the underwater ray march writes into, sized
    /// from the viewport by <see cref="HalfResTarget"/>. Called from
    /// <see cref="LoadResources"/> and <see cref="SetViewport"/>, recreating only when the
    /// half-res size actually changed - two adjacent viewport widths often share one, which is
    /// what keeps a drag-resize from churning GL objects on every pixel of drag.
    /// <para>
    /// Unlike <see cref="CreateReflectionTarget"/> this one needs no validity flag and cannot
    /// flash on a resize: the march covers the target with a full-screen triangle, blending
    /// off and no clear, on every frame it runs, and nothing samples the texture before that
    /// draw. The undefined contents of a fresh allocation are therefore never observable.
    /// </para>
    /// Like its two siblings this assumes <c>ActiveTexture</c> is unit 0 on entry - the
    /// convention <see cref="CopyScene"/> restores before it returns.
    /// </summary>
    private void CreateRaysTarget()
    {
        var desired = HalfResTarget.For(_viewportSize.X, _viewportSize.Y);
        if (!HalfResTarget.ShouldRecreate(_raysFbo != 0, new HalfResTarget.Size(_raysSize.X, _raysSize.Y), desired))
            return;

        DeleteRaysTarget();
        _raysSize = new Vector2D<int>(desired.Width, desired.Height);

        _raysTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _raysTex);
        unsafe
        {
            // Half-float, not Rgba8: alpha carries a marched distance in blocks (up to
            // WaterRayMaxDistance), which a normalised 8-bit channel cannot hold, and 8-bit
            // rgb would re-band the deliberately dithered, dim additive term.
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                (uint)desired.Width, (uint)desired.Height, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        // The upsample fetches texels explicitly and clamps the coordinates itself, so the
        // filter and wrap modes here are belt-and-braces.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _raysFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _raysFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _raysTex, 0);

        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Underwater rays framebuffer incomplete: {status}");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        // Recreate path only (the size-unchanged early return above binds nothing).
        InvalidateTextureUnits();
    }

    private void DeleteRaysTarget()
    {
        if (_raysFbo == 0) return;
        gl.DeleteFramebuffer(_raysFbo);
        gl.DeleteTexture(_raysTex);
        _raysFbo = 0;
        // Deleting a texture reverts its bindings to 0 on every unit of the current context.
        InvalidateTextureUnits();
    }

    /// <summary>Snapshots the default framebuffer's colour and depth into the scene textures so the
    /// water pass can refract what is behind the surface and measure how thick the water is. Two
    /// texture copies, no FBO: the window has no MSAA, so CopyTexSubImage2D reads it directly.</summary>
    private void CopyScene(bool colour)
    {
        if (colour)
        {
            BindUnit(3, TextureTarget.Texture2D, _sceneColorTex);
            gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, (uint)_sceneSize.X, (uint)_sceneSize.Y);
        }
        BindUnit(4, TextureTarget.Texture2D, _sceneDepthTex);
        gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, (uint)_sceneSize.X, (uint)_sceneSize.Y);
        ActiveUnit(0);
    }

    /// <summary>
    /// Renders the opaque world (chunks + LOD, no water, no entities) from a camera
    /// mirrored across the sea surface into the reflection texture, which the water in
    /// <see cref="RenderWorldTranslucent"/> then samples projectively. Below-plane
    /// geometry is clipped via <c>uClipY</c> so the seabed never shows up in the mirror.
    /// Skipped (leaving <c>uReflValid</c> at 0) when there is no water to reflect in, or
    /// when the eye is below the sea plane or submerged in any water
    /// (<see cref="ReflectionReuse.MirrorApplies"/>). Runs after <see cref="RenderShadows"/> so the
    /// shadow map it samples is already filled.
    /// The mirror is re-rendered at most every other frame: a reused frame issues no GL work
    /// at all and simply re-pushes the cached <c>_reflViewProj</c> that produced the texture,
    /// with the refresh decision in <see cref="ReflectionReuse.ShouldRefresh"/> and its
    /// thresholds in <see cref="WorldGenerationConfig"/>.
    /// </summary>
    public void RenderReflection(Camera camera, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<LodRegion> regions)
    {
        worldShader.Use();
        // The target follows the reflection-quality setting: recreating is a no-op while the
        // size is unchanged, so this can run every frame and pick up slider changes live.
        var targetRecreated = CreateReflectionTarget();
        // CullWater ran a moment ago, so a frame with no water on screen skips the whole
        // mirrored render rather than filling a texture nothing will sample. Far-only water
        // draws through the cheap shader path, which never samples the mirror either.
        if (!WorldGenerationConfig.WaterEnabled || !GameSettings.WaterReflections || camera == null || !_waterVisible
            || !_nearWaterVisible || !ReflectionReuse.MirrorApplies(camera.RenderEye.Y, WaterPlaneY, camera.IsEyeInWater)
            || _reflFbo == 0)
        {
            // A frame that fell through here must never be reused from: however old the
            // texture is, it no longer stands in any known relation to the current camera.
            _reflMirrorValid = false;
            _reflReusedLastFrame = false;
            SetWorldUniform("uReflValid", 0f);
            return;
        }

        // Pitch stays within ±89° through the mirror, so UnitY never parallels the view direction.
        var fwd = camera.ViewDirection;
        var pose = new ReflectionReuse.ReflectionPose(camera.RenderEye, fwd, camera.CurrentFov,
            _viewportSize.X / (float)Math.Max(1, _viewportSize.Y), WaterPlaneY);
        if (!ReflectionReuse.ShouldRefresh(_reflPose, pose, _reflMirrorValid, targetRecreated,
                _reflReusedLastFrame))
        {
            _reflReusedLastFrame = true;
            // _reflTex still holds the mirror built from _reflPose, so the water has to project
            // through the matrix that produced it - this frame's would make the mirror swim.
            SetWorldUniform("uReflViewProj", _reflViewProj);
            SetWorldUniform("uReflValid", 1f);
            return;
        }

        // The reflection texture is this pass's render target, so it must not stay bound
        // to unit 2 while it draws: sampling and rendering the same texture is a
        // feedback loop (see RenderShadows for unit 1). Later passes rebind it.
        BindUnit(2, TextureTarget.Texture2D, 0);
        ActiveUnit(0);

        // Mirror position and facing across the horizontal plane.
        var mirroredPos = new Vector3(camera.RenderEye.X, 2f * WaterPlaneY - camera.RenderEye.Y,
            camera.RenderEye.Z);
        var mirroredFwd = new Vector3(fwd.X, -fwd.Y, fwd.Z);
        var view = Matrix4x4.CreateLookAt(mirroredPos, mirroredPos + mirroredFwd, Vector3.UnitY);
        _reflViewProj = view * camera.GetProjectionMatrix();

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflFbo);
        gl.Viewport(_reflSize);
        gl.ClearColor(_skyColor.X, _skyColor.Y, _skyColor.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        // The sun belongs in the mirror for the same reason it does in the real frame: it is
        // painted onto the bare sky before any terrain, and everything after occludes it.
        RenderSun(mirroredFwd, camera.GetProjectionMatrix());
        // RenderSun leaves its own program and its disc on unit 0; the mirrored world draws
        // below want ours and the atlas back.
        worldShader.Use();
        BindUnit(0, TextureTarget.Texture2DArray, textureAtlas);
        gl.Disable(EnableCap.Blend);

        // Fog and specular inside the texture want the mirrored eye; the clip drops
        // everything below the surface. Both are restored below.
        var savedCamPos = _cameraPos;
        var savedClip = _clipY;
        _cameraPos = mirroredPos;
        _clipY = WaterPlaneY;
        ApplyFrameUniforms();
        SetWorldUniform("uReflViewProj", _reflViewProj);

        var frustum = new Frustum(_reflViewProj);

        _visibleChunks.Clear();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (!chunk.HasMesh) continue;

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax)) continue;

            _visibleChunks.Add((chunk, Vector3.DistanceSquared(chunk.WorldMin, mirroredPos)));
        }

        _visibleChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        chunkShader.Use();
        chunkShader.SetUniform(_locChunkViewProj, _reflViewProj);
        _chunkDraws.BeginPass(_visibleChunks.Count);

        foreach (var (chunk, _) in _visibleChunks)
            _chunkDraws.Add(chunk.OpaqueSlice!.Value, chunk.WorldMin,
                MathF.Min(1f, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

        _chunkDraws.Draw(OpaqueChunkGeometry.Vao, chunkShader, _locChunkDrawBase);
        // The LOD regions below ride the same program and the same ring: chunkShader is still in
        // use from the chunk pass above and _locChunkViewProj already holds _reflViewProj.

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

        // Deliberately unsorted: the list above adds (region, 0f) and the mirror has always
        // drawn regions in list order. Do not "fix" it into one of LodRegion's comparators —
        // that would change what the mirror shows, which is not this item's business.

        _chunkDraws.BeginPass(_visibleRegions.Count);

        // The mirror does not advance Lifetime; it reads the value RenderLod left last frame.
        foreach (var (region, _) in _visibleRegions)
            _chunkDraws.Add(region.OpaqueSlice!.Value,
                new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks),
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

        _chunkDraws.Draw(LodGeometry.Vao, chunkShader, _locChunkDrawBase);

        // Clouds in the mirror are what makes the reflection readable on open water (the sky
        // itself is a flat colour). The mirror flips triangle winding, so cull front faces
        // here to keep the same "nearest exterior surface wins" behaviour RenderClouds relies on.
        if (_clouds != null && savedCamPos.Y >= WaterPlaneY)
        {
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Front);
            _clouds.Render(_reflViewProj, mirroredPos, _time, _sunDir, _sunColor, _sunIntensity, _ambient,
                _fogColor, _fogNear, _fogFar);
            gl.CullFace(TriangleFace.Back);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(true);
            // _clouds.Render bound its own program; ApplyFrameUniforms below needs ours.
            worldShader.Use();
        }

        _cameraPos = savedCamPos;
        _clipY = savedClip;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(_viewportSize);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Restores the real eye's fog/camera uniforms; uReflViewProj survives in the program.
        ApplyFrameUniforms();
        SetWorldUniform("uReflValid", 1f);
        // _reflTex now matches this camera, so the next frame may reuse it (once).
        _reflPose = pose;
        _reflMirrorValid = true;
        _reflReusedLastFrame = false;
    }

    private static float Smooth01(float t) => MathUtils.SmoothStep(t);

    private static Vector3 Mix(Vector3 a, Vector3 b, float t) => a.Lerp(b, t);

    /// <summary>
    /// Clears to the sky colour, paints the sun into it, and leaves the GL state the world
    /// passes expect. <paramref name="camera"/> only orients the sun quad; a null camera just
    /// skips it.
    /// </summary>
    public void BeginFrame(Camera? camera)
    {
        // The once-a-frame hard reset for the texture-unit cache: the UI pass, the particle
        // renderers and anything that rebuilt a GL texture ran since the last one.
        InvalidateTextureUnits();
        gl.ClearColor(_skyColor.X, _skyColor.Y, _skyColor.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Straight onto the fresh sky, before any terrain: see RenderSun
        if (camera != null)
            RenderSun(camera.ViewDirection, camera.GetProjectionMatrix());

        // Exactly once a frame, before any pass appends to it: the ring rolls onto its next
        // slot and waits on the fence that says the GPU is done reading it.
        _chunkDraws.BeginFrame();

        chunkShader.Use();
        // Opaque default: punch the leaf tiles' holes; the translucent pass lowers it.
        chunkShader.SetUniform(_locChunkAlphaCutoff, WorldGenerationConfig.AlphaCutoff);
        worldShader.Use();
        worldShader.SetUniform(_locAlphaCutoff, WorldGenerationConfig.AlphaCutoff);
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
    /// Takes the facing and projection rather than a camera, so the reflection pass can draw
    /// the same disc through its mirrored forward vector.
    /// </summary>
    private void RenderSun(Vector3 forward, Matrix4x4 projection)
    {
        if (_sunVisibility <= 0f) return;

        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        sunShader.Use();
        BindUnit(0, TextureTarget.Texture2DArray, _sunTexture);

        // Rotation only: dropping the camera position is what pins the quad at infinity
        var viewRot = Matrix4x4.CreateLookAt(Vector3.Zero, forward, Vector3.UnitY);
        sunShader.SetUniform("uViewProj", viewRot * projection);
        sunShader.SetUniform("uSunDir", _sunDir);
        // The quad has to hold the whole glow, so it is sized to the glow's angular radius and
        // the shader keeps the textured disc inside the inner uDiscFraction of it. The unit
        // square's corners sit at +-0.5, hence the factor 2 to make uHalfSize the true radius.
        sunShader.SetUniform("uHalfSize", 2f * 100f * MathF.Tan(GameSettings.SunSize
            * WorldGenerationConfig.SunGlowScale * MathF.PI / 180f));
        sunShader.SetUniform("uDiscFraction", 1f / WorldGenerationConfig.SunGlowScale);
        sunShader.SetUniform("uGlowIntensity", GameSettings.SunGlow);
        sunShader.SetUniform("uGlowExponent", WorldGenerationConfig.SunGlowExponent);
        sunShader.SetUniform("uSunColor", _sunColor);
        sunShader.SetUniform("uVisibility", _sunVisibility);

        _sunQuad.Render();

        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
    }

    // PERF_TODO R5. What the context already has selected and bound on units 0-4. Every
    // render-path ActiveTexture/BindTexture in this class goes through the three primitives
    // below, so a redundant one costs nothing; everything that can change those units behind
    // their back calls InvalidateTextureUnits (see its doc comment for the full audit).
    // Render thread only, like every other GL call here (AGENTS.md) - plain field, no lock.
    private readonly TextureUnitCache _texUnits = new();

    /// <summary>Selects a texture unit. Issued wherever this class used to issue a bare
    /// <c>gl.ActiveTexture</c>, 1:1.</summary>
    private void ActiveUnit(int unit)
    {
        if (_texUnits.MarkActive(unit)) gl.ActiveTexture(TextureUnit.Texture0 + unit);
    }

    /// <summary>
    /// Replaces a <c>gl.ActiveTexture</c> + <c>gl.BindTexture</c> pair, 1:1. The select is
    /// unconditional work for the cache (it may still skip the GL call), so the active unit
    /// after this call is exactly what it is without the cache - which matters because
    /// <see cref="CopyScene"/>'s <c>glCopyTexSubImage2D</c>, the three <c>Create*Target</c>
    /// methods (see the remark on <see cref="CreateRaysTarget"/>) and
    /// <see cref="TextureLoader.Upload"/> all act on whatever unit happens to be active. Only
    /// the redundant bind is elided here, never the trajectory.
    /// </summary>
    private void BindUnit(int unit, TextureTarget target, uint texture)
    {
        ActiveUnit(unit);
        if (_texUnits.MarkBound(unit, (int)target, texture)) gl.BindTexture(target, texture);
    }

    /// <summary>
    /// <see cref="BindWorldTextures"/> ONLY. Skips the select as well when the binding already
    /// matches, which is what turns a fully redundant replay into zero GL calls. Safe by a
    /// purely local argument: nothing between BindWorldTextures' five binds reads the active
    /// unit, and the method ends with an unconditional <c>ActiveUnit(0)</c> that restores its
    /// documented postcondition. Do not use it anywhere else without repeating that argument -
    /// in particular not near a copy, an upload, or a call out to another class.
    /// </summary>
    private void BindLazy(int unit, TextureTarget target, uint texture)
    {
        if (_texUnits.Matches(unit, (int)target, texture)) return;
        ActiveUnit(unit);
        gl.BindTexture(target, texture);
        _texUnits.MarkBound(unit, (int)target, texture);
    }

    /// <summary>
    /// Forgets the tracked unit state, so the next <see cref="BindUnit"/> / <see cref="BindLazy"/>
    /// re-issues its GL calls. This is the maintained audit of every site that can change unit 0-4
    /// or the active unit behind the primitives' back; each one calls this.
    /// <para><b>Invalidates:</b>
    /// <see cref="BeginFrame"/> (the once-a-frame hard reset);
    /// <see cref="LoadResources"/> (the sun upload, <c>TextureAtlas.Build</c>,
    /// <c>TextureLoader.LoadFromFile</c> and the target creators all bind raw);
    /// <see cref="CreateShadowMap"/>;
    /// <see cref="CreateReflectionTarget"/> on its recreate path only (it is called every frame and
    /// early-returns without binding when the size is unchanged) and
    /// <see cref="DeleteReflectionTarget"/>;
    /// <see cref="CreateSceneCopy"/> / <see cref="DeleteSceneCopy"/>;
    /// <see cref="CreateRaysTarget"/> / <see cref="DeleteRaysTarget"/>;
    /// <see cref="SetViewport"/>;
    /// <see cref="RenderLeafParticles"/> and <see cref="RenderBubbleParticles"/> (both renderers
    /// bind their own sprite to unit 0);
    /// <see cref="RenderUnderwaterRays"/> (the rays renderer detours through unit 5);
    /// <see cref="RenderUI"/> (the font and the item icons take unit 0, and this pass spans the
    /// frame boundary);
    /// and <c>ItemMeshCache.BuildTexture</c> through its <c>GlStateChanged</c> callback - item
    /// textures are built lazily <em>inside</em> the entity pass and <c>TextureLoader.Upload</c>
    /// binds on whatever unit is live. Every <c>glDeleteTextures</c> is in this list too, because
    /// deleting a texture reverts its bindings to 0 in the current context; the only ones that are
    /// not are <see cref="Dispose"/>'s and the other classes' Dispose-time deletes, which run after
    /// the last frame.
    /// </para>
    /// <para><b>Provably cannot invalidate</b>, so do not add a call "just in case":
    /// <c>CloudRenderer</c>, <c>FireParticleRenderer</c> and the selection pass contain no
    /// <c>ActiveTexture</c>/<c>BindTexture</c> at all - their VAO/VBO binds are buffer
    /// state, and the program they switch is handled by <c>Shader.Use</c>'s own cache.
    /// <see cref="RenderBlockBreaking"/> binds its crack sprite through <see cref="BindUnit"/>,
    /// which updates the cache rather than invalidating it, so it needs no call either.
    /// <c>ChunkDrawBatch</c>, <c>ChunkGeometryPool</c> and <c>Mesh.Render</c> touch VAO/VBO/indirect
    /// state only. <see cref="CopyScene"/>'s <c>glCopyTexSubImage2D</c> changes texture
    /// <em>contents</em>, not bindings (its own select/bind pair is routed through
    /// <see cref="BindUnit"/> precisely because the copy destination is the active unit's binding).
    /// <c>BitmapFont</c>'s texture creation runs in the <c>UIRenderer</c> constructor, before the
    /// first frame. <see cref="SetLighting"/> runs between the UI pass and <see cref="BeginFrame"/>
    /// and issues no texture bind, which is what makes the top of BeginFrame a valid reset point.
    /// <see cref="RenderSun"/> early-returns on invisibility <em>before</em> its unit-0 bind, so on
    /// a night frame it leaves unit 0 alone - BeginFrame's reset covers both cases.
    /// </para>
    /// </summary>
    private void InvalidateTextureUnits() => _texUnits.Invalidate();

    /// <summary>
    /// Binds the world textures for a world draw: the atlas on unit 0, the shadow map on
    /// unit 1, the reflection texture on unit 2 and the scene colour/depth copies on units 3
    /// and 4. Unit 0 is left active on the way out, because <see cref="RenderItemDrops"/>
    /// binds an item's own texture assuming that; the trailing unconditional
    /// <see cref="ActiveUnit"/> call is what upholds that postcondition now that the five binds
    /// themselves may skip their selects.
    /// <para>PERF_TODO R5: this runs ~18x a frame, most of them back-to-back inside the entity
    /// pass, and it used to cost 10 GL calls every time. A replay whose five bindings all still
    /// match costs 0; the dominant entity-pass case - unit 0 swapped away to an item sprite and
    /// back - costs 1.</para>
    /// </summary>
    private void BindWorldTextures()
    {
        BindLazy(1, TextureTarget.Texture2D, _shadowDepthTex);
        BindLazy(2, TextureTarget.Texture2D, _reflTex);
        BindLazy(3, TextureTarget.Texture2D, _sceneColorTex);
        BindLazy(4, TextureTarget.Texture2D, _sceneDepthTex);
        BindLazy(0, TextureTarget.Texture2DArray, textureAtlas);
        BindLazy(MaterialUnit, TextureTarget.Texture2DArray, materialAtlas);
        ActiveUnit(0);
    }

    /// <summary>
    /// Pushes every world-shader uniform that is constant for the whole frame onto *both* world
    /// programs — the plain one and the CHUNK_DRAW variant the chunk passes draw through, which
    /// has its own copy of every uniform. Called once per frame from <see cref="BeginFrame"/> —
    /// uniform values live in the program object and survive any number of <c>UseProgram</c>
    /// switches, so the selection, break, sun and UI shaders running in between do not disturb
    /// these and every later world pass in the frame inherits them. Everything it reads is final
    /// by then: <see cref="SetLighting"/> (including <c>_lightViewProj</c>) runs before
    /// <see cref="BeginFrame"/>, and nothing in a world pass writes these fields.
    /// <c>uFlash</c>/<c>uHitFlash</c> are entity-only (primed TNT / hurt slimes), so they are
    /// reset on the plain program alone.
    /// Leaves <c>worldShader</c> bound, which is what every caller assumes.
    /// </summary>
    private void ApplyFrameUniforms()
    {
        chunkShader.Use();
        ApplyFrameUniformsTo(chunkShader, _locChunkPointPos, _locChunkPointColor);
        worldShader.Use();
        ApplyFrameUniformsTo(worldShader, _locPointPos, _locPointColor);
        worldShader.SetUniform(_locFlash, 0f);
        worldShader.SetUniform(_locHitFlash, 0f);
    }

    /// <summary>The body of <see cref="ApplyFrameUniforms"/>, run once per world program.
    /// <paramref name="program"/> must already be bound.</summary>
    private void ApplyFrameUniformsTo(Shader program, int[] locPointPos, int[] locPointColor)
    {
        program.SetUniform("uTime", _time);
        program.SetUniform("uSunDir", _sunDir);
        program.SetUniform("uSunColor", _sunColor);
        program.SetUniform("uSunIntensity", _sunIntensity);
        program.SetUniform("uAmbient", _ambient);
        program.SetUniform("uTorchColor", _torchColor);
        program.SetUniform("uTorchIntensity", TorchIntensity);
        program.SetUniform("uFogColor", _fogColor);
        program.SetUniform("uFogNear", _fogNear);
        program.SetUniform("uFogFar", _fogFar);
        program.SetUniform("uCameraPos", _cameraPos);
        program.SetUniform("uNumPointLights", _pointCount);
        program.SetUniformArray3(locPointPos, _pointPos);
        program.SetUniformArray3(locPointColor, _pointColors);
        program.SetUniform("uSwayStrength", GameSettings.LeafSway);
        // The water look rides here (not the one-time init) so the settings sliders act live.
        program.SetUniform("uWaterNormalScale", GameSettings.WaterWaves);
        program.SetUniform("uWaterRefractStrength", GameSettings.WaterRefraction);
        program.SetUniform("uWaterCausticStrength", GameSettings.WaterCaustics);
        program.SetUniform("uLightViewProj", _lightViewProj);
        program.SetUniform("uShadowStrength", _shadowStrength);
        program.SetUniform("uShadowTexel", 1f / WorldGenerationConfig.ShadowMapSize);
        program.SetUniform("uShadowWorldTexel",
            2f * WorldGenerationConfig.ShadowRadiusBlocks / WorldGenerationConfig.ShadowMapSize);
        program.SetUniform("uShadowSoftness", WorldGenerationConfig.ShadowSoftnessTexels);
        program.SetUniform("uShadowNormalOffset", WorldGenerationConfig.ShadowNormalOffsetTexels);
        program.SetUniform("uShadowBiasTexels", WorldGenerationConfig.ShadowBiasTexels);
        // World blocks -> uvz.z: the ortho box's 4r-block depth range is halved into 0.5 of the
        // uvz range, so one whole uvz unit is 8r blocks.
        program.SetUniform("uShadowBiasScale", 1f / (8f * WorldGenerationConfig.ShadowRadiusBlocks));
        program.SetUniform("uClipY", _clipY);
        // The water pass reads the scene copies by screen uv and unprojects their depth; both
        // describe the real eye, so the reflection pass (which draws no water) can ignore them.
        program.SetUniform("uInvViewport",
            new Vector2(1f / Math.Max(1, _viewportSize.X), 1f / Math.Max(1, _viewportSize.Y)));
        program.SetUniform("uInvViewProj", _invViewProj);
        program.SetUniform("uShadowStrengthDry", _shadowStrengthDry);
        program.SetUniform("uUnderwater", _eyeInLava ? 0f : MathUtils.SmoothStep(_underwaterT));
    }

    /// <summary>
    /// Everything a single world draw has to say: its model matrix (the vertex shader needs it
    /// for world positions and normals), its full MVP and its fade alpha. Exactly three
    /// <c>glUniform</c> calls through cached locations — the rest of the world shader's state
    /// was uploaded once by <see cref="ApplyFrameUniforms"/>.
    /// Entity draws only: chunks and LOD regions both go through <see cref="ChunkDrawBatch"/>,
    /// where a uniform could not vary between the sub-draws of one multi-draw anyway.
    /// </summary>
    private void SetDrawUniforms(in Matrix4x4 model, in Matrix4x4 mvp, float alpha)
    {
        worldShader.SetUniform(_locModel, model);
        worldShader.SetUniform(_locMvp, mvp);
        worldShader.SetUniform(_locAlpha, alpha);
    }

    /// <summary>
    /// Draws the cloud field once, translucent, after every opaque pass and the water pass so the
    /// terrain and the sea surface both occlude it properly. Skipped with the eye underwater, where
    /// the water fog already hides the sky. Only the cull state has to be restored afterwards:
    /// blending is the state the later passes expect anyway.
    /// </summary>
    public void RenderClouds(Camera? camera, bool eyeInWater)
    {
        if (camera == null || eyeInWater || _clouds == null) return;

        // Translucent and depth-tested: terrain and water (already drawn) occlude the clouds, and a
        // depth pre-pass inside CloudRenderer picks the nearest surface so overlapping rounded pieces
        // blend exactly once whatever their draw order. Back-face culling keeps bottoms from bleeding
        // through tops; DepthMask goes back on below because the pre-pass leaves it off.
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(false);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        _clouds.Render(camera.GetViewProjection(), _cameraPos, _time,
            _sunDir, _sunColor, _sunIntensity, _ambient, _fogColor, _fogNear, _fogFar);
        gl.Disable(EnableCap.CullFace);
        gl.DepthMask(true);
    }

    /// <summary>Sun shafts through the water, drawn while the eye is submerged. Needs the depth of
    /// everything drawn so far (water surface included), so the depth texture is re-copied here
    /// before the march.
    /// The march itself runs at HALF resolution into <see cref="_raysFbo"/> and is composited back
    /// to full resolution by a depth-aware upsample (PERF_TODO R2) - a quarter of the marched
    /// pixels, with the loop's step count, sample count and per-sample formula untouched.
    /// The depth copy stays FULL resolution deliberately: the composite runs at full-res pixels and
    /// weighs each half-res tap against the distance the full-res pixel marches, so it needs a
    /// full-res depth reference, and the default framebuffer's own depth is unsamplable while it is
    /// the draw target (which is why CopyScene exists). A half-res depth alongside this one would be
    /// additive cost, not a saving - see R3 in PERF_TODO.md, which shares this machinery.
    /// Both draws sit inside this one method, so the RaysPass GPU bracket around the call in
    /// World.RenderFrame still measures the whole pass and FrameProfiler needs no change.</summary>
    public void RenderUnderwaterRays(Camera? camera)
    {
        if (camera == null || _rays == null || _eyeInLava) return;
        var wet = MathUtils.SmoothStep(_underwaterT);
        var strength = wet * _shadowStrengthDry * _dayness * GameSettings.WaterSunShafts;
        if (strength <= 0.0005f) return;
        if (_raysFbo == 0) return;             // no target to march into (never happens after LoadResources)
        CopyScene(colour: false);
        BindUnit(1, TextureTarget.Texture2D, _shadowDepthTex);
        BindUnit(4, TextureTarget.Texture2D, _sceneDepthTex);
        ActiveUnit(0);
        _rays.Render(_raysFbo, _raysTex, _raysSize, _viewportSize, _invViewProj, _cameraPos, _sunDir,
            _sunColor, strength, WaterPlaneY, _lightViewProj, _time);
        // UnderwaterRaysRenderer selects unit 5 for the upsample and puts unit 0 back. Unit 5 is
        // untracked so the records happen to survive, but the renderer does not promise that.
        InvalidateTextureUnits();
    }

    // Wall-clock timestamp of the previous RenderWorld, so the chunk fade-in can advance by
    // real delta time instead of an assumed 60 Hz step (PERF_TODO item 10).
    private double _lastWorldTime;

    // Reused across frames to avoid a per-frame allocation
    private readonly List<(Chunk chunk, float distSq)> _visibleChunks = new(1024);

    /// <summary>
    /// Draws the opaque world. <paramref name="chunks"/> is
    /// <see cref="ChunkManager.Renderable"/>: every chunk with something to draw, opaque or
    /// translucent — the water-only ones still need their fade lifetime advanced here.
    /// The surviving chunks are frustum-culled and sorted front to back on the CPU exactly as
    /// before, but they are then appended to <see cref="ChunkDrawBatch"/> and issued as one
    /// <c>glMultiDrawElementsIndirect</c> out of the opaque <see cref="ChunkGeometryPool"/>.
    /// </summary>
    public void RenderWorld(Camera camera, IReadOnlyList<Chunk> chunks, double time)
    {
        if (camera == null) return;

        // time is _window.Time; the first frame has no previous stamp, so it falls back to one
        // 60 Hz step. The clamp keeps a hitch (or a debugger break) from advancing every
        // streaming chunk straight to full alpha.
        var dt = _lastWorldTime > 0 ? MathUtils.Clamp(time - _lastWorldTime, 0.0, 0.1) : 1.0 / 60.0;
        _lastWorldTime = time;

        chunkShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        chunkShader.SetUniform(_locChunkViewProj, viewProj);
        var frustum = camera.GetViewFrustum();
        var camChunkPos = (camera.RenderEye / Chunk.Size).Floor();

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

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax))
                continue;

            var distSq = Vector3.DistanceSquared(chunk.Position, camChunkPos);
            _visibleChunks.Add((chunk, distSq));
        }

        // Sort by distance (front to back for better z-buffer performance)
        _visibleChunks.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        LastVisibleChunkCount = _visibleChunks.Count;

        _chunkDraws.BeginPass(_visibleChunks.Count);

        foreach (var (chunk, _) in _visibleChunks)
        {
            // Real delta time: the fade (Chunk.MAX_LIFETIME, 0.5 s) now takes 0.5 s of wall
            // clock at any frame rate instead of 30 frames.
            chunk.lifetime += dt;
            // Water-only chunks are in the list purely to have their lifetime advanced
            if (chunk.OpaqueSlice is not { } slice) continue;

            _chunkDraws.Add(slice, chunk.WorldMin,
                MathF.Min(1F, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));
        }

        _chunkDraws.Draw(OpaqueChunkGeometry.Vao, chunkShader, _locChunkDrawBase);
    }

    // Reused across frames, like _visibleChunks / _visibleRegions
    private readonly List<(Chunk chunk, float distSq)> _translucentChunks = new(256);
    private readonly List<(LodRegion region, float distSq)> _translucentRegions = new(256);

    // Whether anything the translucent pass will draw is actually water, recomputed by
    // CullWater once a frame. Glass alone does not turn the water-only passes back on.
    private bool _waterVisible;
    // Whether any of that water is near enough for the fancy model (nearer than
    // WaterFancyFarBlocks). Far-only water draws through the cheap shader path, which
    // samples neither the reflection nor the scene copies, so both passes are skipped.
    private bool _nearWaterVisible;

    /// <summary>Frustum-culls the translucent draws once per frame and records whether any water
    /// (chunk or LOD sheet) is on screen. Reflection and the scene copies only run when it is,
    /// and RenderWorldTranslucent draws from the lists filled here. Glass-only chunks are listed
    /// for drawing but never trigger the water-only passes.
    /// Takes the translucent subset (<see cref="ChunkManager.TranslucentRenderable"/>), not all
    /// meshed chunks.</summary>
    public void CullWater(Camera camera, IReadOnlyList<Chunk> chunks, IReadOnlyList<LodRegion> regions)
    {
        _translucentChunks.Clear();
        _translucentRegions.Clear();
        _waterVisible = false;
        _nearWaterVisible = false;
        if (camera == null) return;

        var frustum = camera.GetViewFrustum();
        var camChunkPos = (camera.RenderEye / Chunk.Size).Floor();
        var camPos = camera.RenderEye;
        var fancyFarSq = WorldGenerationConfig.WaterFancyFarBlocks * WorldGenerationConfig.WaterFancyFarBlocks;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (!chunk.HasTranslucentMesh)
                continue;

            if (!frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax))
                continue;

            _translucentChunks.Add((chunk, Vector3.DistanceSquared(chunk.Position, camChunkPos)));
            if (chunk.HasWaterMesh)
            {
                _waterVisible = true;
                var center = chunk.WorldMin + new Vector3(Chunk.Size / 2f);
                if (Vector3.DistanceSquared(center, camPos) <= fancyFarSq)
                    _nearWaterVisible = true;
            }
        }

        // Back to front, so overlapping surfaces (a pond seen across a sea) blend in the
        // order the eye sees them
        _translucentChunks.Sort((a, b) => b.distSq.CompareTo(a.distSq));

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
            var regionDistSq = Vector3.DistanceSquared(center, camPos);
            _translucentRegions.Add((region, regionDistSq));
            // A LOD region's translucent mesh is only ever the water sheet
            _waterVisible = true;
            if (regionDistSq <= fancyFarSq)
                _nearWaterVisible = true;
        }

        // Finest level first, then back to front within a level. The rationale lives on
        // LodRegion.CompareWaterDraw, which is pinned headlessly (LodDrawOrderTests); since R10
        // this ordering is also the glMultiDrawElementsIndirect submission order.
        _translucentRegions.Sort((a, b) =>
            LodRegion.CompareWaterDraw(a.region.Level, a.distSq, b.region.Level, b.distSq));
    }

    /// <summary>
    /// Draws every fluid surface — chunk water first, then the far LOD's water sheets — in one
    /// blended pass after the whole opaque world, so terrain, drops and TNT behind the water
    /// show through it. Depth writing stays on: water bodies are at most a couple of layers
    /// deep from any viewpoint, and writing depth is what makes the chunk-vs-LOD overlap at
    /// the full-detail ring resolve by distance instead of by draw order.
    /// The lists it walks were filled by <see cref="CullWater"/> earlier in the frame, which
    /// also decided whether the scene copy below is worth taking.
    /// </summary>
    public void RenderWorldTranslucent(Camera camera)
    {
        if (camera == null) return;

        // The refraction reads the frame as it stands right now: everything opaque plus the
        // entities, and nothing translucent yet. Depth comes along for the thickness measure.
        // Skipped for far-only water: the cheap shader path samples neither copy.
        if (_nearWaterVisible) CopyScene(colour: true);

        chunkShader.Use();
        // No cutout here: glass's faint interior must blend instead of discarding away.
        chunkShader.SetUniform(_locChunkAlphaCutoff, 0f);
        BindWorldTextures();
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(true);

        var viewProj = camera.GetViewProjection();
        chunkShader.SetUniform(_locChunkViewProj, viewProj);

        _chunkDraws.BeginPass(_translucentChunks.Count);

        foreach (var (chunk, _) in _translucentChunks)
            _chunkDraws.Add(chunk.TranslucentSlice!.Value, chunk.WorldMin,
                MathF.Min(1f, (float)(chunk.lifetime / Chunk.MAX_LIFETIME)));

        _chunkDraws.Draw(TranslucentChunkGeometry.Vao, chunkShader, _locChunkDrawBase);

        // The LOD water sheets ride the same program, the same ring and the same cutout-free
        // state the chunk water just used; only the pool VAO differs. Chunk translucent first,
        // LOD water second, exactly as before.
        _chunkDraws.BeginPass(_translucentRegions.Count);

        foreach (var (region, _) in _translucentRegions)
            _chunkDraws.Add(region.WaterSlice!.Value,
                new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks),
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));

        _chunkDraws.Draw(LodGeometry.Vao, chunkShader, _locChunkDrawBase);

        // Later chunk passes reuse this program and need the cutout behaviour back.
        chunkShader.SetUniform(_locChunkAlphaCutoff, WorldGenerationConfig.AlphaCutoff);
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

        chunkShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        chunkShader.SetUniform(_locChunkViewProj, viewProj);
        var frustum = camera.GetViewFrustum();
        var camPos = camera.RenderEye;

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

        // Coarsest level first, then front to back within a level. The rationale lives on
        // LodRegion.CompareOpaqueDraw, which is pinned headlessly (LodDrawOrderTests); since R10
        // this ordering is also the glMultiDrawElementsIndirect submission order.
        _visibleRegions.Sort((a, b) =>
            LodRegion.CompareOpaqueDraw(a.region.Level, a.distSq, b.region.Level, b.distSq));

        _chunkDraws.BeginPass(_visibleRegions.Count);

        foreach (var (region, _) in _visibleRegions)
        {
            region.Lifetime += 1.0 / 60.0;

            _chunkDraws.Add(region.OpaqueSlice!.Value,
                new Vector3(region.X * region.SizeBlocks, 0f, region.Z * region.SizeBlocks),
                MathF.Min(1f, (float)(region.Lifetime / LodRegion.MAX_LIFETIME)));
        }

        // chunkShader's uAlphaCutoff is already 0.5 (set once at construction and restored by
        // BeginFrame), which is exactly what worldShader carried here.
        _chunkDraws.Draw(LodGeometry.Vao, chunkShader, _locChunkDrawBase);
    }

    /// <summary>
    /// Draws each of the block's hitboxes as a box sampling its crop of the block's face
    /// tiles (see <see cref="PopBoxEmitter"/>), so the mining pop starts from the block's real
    /// shape with the chunk mesh's texel density: at grow 0 the boxes sit exactly inside their
    /// outlines, and a hit grows every axis by the same absolute amount, so tiny blocks jump
    /// as visibly as full cubes. Grown, the boxes enclose the block baked into the chunk mesh,
    /// so the block appears to jump out of the terrain.
    /// </summary>
    public void RenderBlockPop(Camera camera, Vector3 blockPosition, byte blockId, float grow,
        BlockHitbox[]? boxes = null)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return;

        boxes ??= BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        if (boxes.Length > _popBoxCapacity)
            GrowPopBuffers(boxes.Length);

        _popVerts.Clear();
        _popIndices.Clear();
        ushort indexOffset = 0;
        var grownVec = new Vector3(grow);
        foreach (var box in boxes)
        {
            var half = (box.Size + grownVec) * 0.5f;
            var grown = new BlockHitbox(box.Center - half, box.Center + half);
            PopBoxEmitter.EmitBox(_popVerts, _popIndices, ref indexOffset,
                grown, box, blockPosition, type.FaceTiles);
        }
        if (_popVerts.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        gl.BindVertexArray(_popVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _popVbo);
        gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_popVerts));
        var matrix = Matrix4x4.Identity;
        SetDrawUniforms(matrix, matrix * camera.GetViewProjection(), 1f);
        unsafe
        {
            gl.DrawElements(PrimitiveType.Triangles, (uint)_popIndices.Count,
                DrawElementsType.UnsignedShort, null);
        }
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the floating item drops, each spinning about its own centre and pulsing in scale.
    /// Block drops draw their atlas cube; pure items (see <see cref="Item"/>) draw their
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
            if (ItemRegistry.IsItem(drop.BlockId))
            {
                if (!itemMeshes.TryGet(drop.BlockId, out mesh, out var itemTexture)) continue;
                BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
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

    public void RenderThrown(Camera camera, IReadOnlyList<ThrownProjectile> thrown)
    {
        if (camera == null || thrown.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var projectile in thrown)
        {
            Mesh? mesh;
            if (ItemRegistry.IsItem(projectile.ItemId))
            {
                if (!itemMeshes.TryGet(projectile.ItemId, out mesh, out var itemTexture)) continue;
                BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    BindWorldTextures();
                    atlasBound = true;
                }

                mesh = blockCubes.Get(projectile.ItemId);
                if (mesh == null) continue;
            }

            var model = Matrix4x4.CreateScale(projectile.Scale)
                        * Matrix4x4.CreateRotationY(projectile.Spin)
                        * Matrix4x4.CreateTranslation(projectile.Position);

            SetDrawUniforms(model, model * viewProj, 1f);
            mesh.Render();
        }
    }

    /// <summary>
    /// The quern's spinning top as a standalone mesh: the plate and handle quads above the
    /// air gap of its baked model (see <see cref="QuernRenderer"/>), centred on the origin
    /// so the render pass can yaw it about the cell centre. Like the other content errors,
    /// a quern that baked with no top quads fails fast here rather than rendering half.
    /// </summary>
    private Mesh BuildQuernTopMesh()
    {
        var model = ContentRegistry.RequireBlock("quern").BakedModel
            ?? throw new InvalidOperationException("Block 'quern' has no baked model.");
        var top = new List<BakedQuad>();
        foreach (var quad in model.Quads)
            if (QuernRenderer.IsTopQuad(quad))
                top.Add(quad);
        if (top.Count == 0)
            throw new InvalidOperationException(
                "Block 'quern' baked with no top quads above the spinning split; check its model's air gap.");
        return Primitives.CreateBlockModel(gl, top);
    }

    public void RenderCampfireCooking(Camera camera, CampfireManager campfires)
    {
        if (camera == null || campfires == null) return;

        var drew = false;
        var atlasBound = true;
        var viewProj = camera.GetViewProjection();
        foreach (var (cell, slot, itemId, _, _) in campfires.CookingView())
        {
            if (!drew)
            {
                worldShader.Use();
                BindWorldTextures();
                drew = true;
            }

            Mesh? mesh;
            if (ItemRegistry.IsItem(itemId))
            {
                if (!itemMeshes.TryGet(itemId, out mesh, out var itemTexture)) continue;
                BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    BindWorldTextures();
                    atlasBound = true;
                }
                mesh = blockCubes.Get(itemId);
                if (mesh == null) continue;
            }

            var scale = ItemDrop.Size * (ItemRegistry.IsItem(itemId) ? 1.5f : 1f);
            var offset = CampfireManager.SlotOffsets[slot % CampfireManager.SlotOffsets.Length];
            var placed = Matrix4x4.CreateScale(scale)
                        * Matrix4x4.CreateRotationY(slot * MathF.PI / 2f)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f, 0f, 0.5f) + offset);

            SetDrawUniforms(placed, placed * viewProj, 1f);
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws every touched quern's spinning top yawed about its cell centre, plus its waiting
    /// input hovering over the plate. The chunk holds only the base (see
    /// <see cref="QuernRenderer"/>), so this entity is what completes the quern — at rest
    /// angle it sits exactly where the static model would. Tops skip the shadow pass, like
    /// the campfire food below.
    /// </summary>
    public void RenderQuerns(Camera camera, QuernManager querns)
    {
        if (camera == null || querns == null || !querns.HasAny || _quernTop == null) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (cell, angle, inputId) in querns.QuernView())
        {
            // The top mesh is centred on its own origin, so it yaws in place
            var center = cell + new Vector3(0.5f, 0.5f, 0.5f);
            var model = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(center);
            SetDrawUniforms(model, model * viewProj, 1f);
            _quernTop.Render();

            if (inputId == 0) continue;

            Mesh? mesh;
            if (ItemRegistry.IsItem(inputId))
            {
                if (!itemMeshes.TryGet(inputId, out mesh, out var itemTexture)) continue;
                BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    BindWorldTextures();
                    atlasBound = true;
                }
                mesh = blockCubes.Get(inputId);
                if (mesh == null) continue;
            }

            // Hovering over the plate, clear of the handle's orbit
            var scale = ItemDrop.Size * (ItemRegistry.IsItem(inputId) ? 1.5f : 1f);
            var placed = Matrix4x4.CreateScale(scale)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.35f, 0.82f, 0.35f));
            SetDrawUniforms(placed, placed * viewProj, 1f);
            mesh.Render();
        }
    }

    /// <summary>
    /// The crucible bowl a kiln holds, as a standalone mesh: the chunk meshes only the kiln's
    /// brickwork, so the vessel standing in its opening is an entity draw like the quern's top.
    /// </summary>
    private Mesh BuildCrucibleMesh()
    {
        var model = ContentRegistry.RequireItem("crucible").BakedModel
            ?? throw new InvalidOperationException(
                "Item 'crucible' has no baked model; its JSON must name one under model.");
        return Primitives.CreateBlockModel(gl, model);
    }

    /// <summary>
    /// The flat liquid quad of one molten material, built on first sight and kept for the run.
    /// Every pool of that material — in a crucible or in a mold — draws this one mesh.
    /// </summary>
    private Mesh MoltenQuad(MoltenMaterial material)
    {
        if (_moltenQuads.TryGetValue(material.Id, out var mesh)) return mesh;
        mesh = Primitives.CreateFlatQuad(gl, material.Color, UvRect.Tile(material.Tile));
        _moltenQuads[material.Id] = mesh;
        return mesh;
    }

    /// <summary>
    /// Draws what a kiln holds inside its opening: the crucible bowl, the molten pool rising in
    /// it, the lump of solid charge still waiting to melt over its rim, and the stored fuel
    /// lying on the hearth. The pool's height comes from
    /// <see cref="KilnManager.DisplayMoltenMl"/>, which carries the fraction of the melt tick in
    /// flight, so the surface rises smoothly instead of stepping once per chunk melted; the lump
    /// is its complement (<see cref="KilnManager.DisplayPendingMl"/> through
    /// <see cref="KilnRenderer.PendingScale01"/>), so what the pool gains the lump loses.
    /// </summary>
    public void RenderKilns(Camera camera, KilnManager kilns)
    {
        if (camera == null || kilns == null || !kilns.HasAny || _crucibleMesh == null) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (anchor, facing, state) in kilns.KilnView())
        {
            if (!atlasBound)
            {
                BindWorldTextures();
                atlasBound = true;
            }

            if (state.HasCrucible)
            {
                // CreateBlockModel centres a cell's model on the origin, so lift by half a block
                // to put the bowl's floor back on the hearth. The bowl is symmetric: no yaw.
                var bowl = KilnRenderer.ToWorld(anchor, facing, KilnRenderer.CrucibleLocal);
                var model = Matrix4x4.CreateTranslation(bowl + new Vector3(0f, 0.5f, 0f));
                SetDrawUniforms(model, model * viewProj, 1f);
                _crucibleMesh.Render();

                var ml = KilnManager.DisplayMoltenMl(state);
                if (ml > 0f && ContentRegistry.MoltenMaterialById(state.Contents.MaterialId) is { } material)
                {
                    var liquid = Matrix4x4.CreateScale(KilnRenderer.CrucibleInnerMax - KilnRenderer.CrucibleInnerMin)
                                * Matrix4x4.CreateTranslation(bowl + new Vector3(0f,
                                    KilnRenderer.LiquidHeight(ml, KilnManager.CrucibleCapacityOf(state)), 0f));
                    SetDrawUniforms(liquid, liquid * viewProj, 1f);
                    MoltenQuad(material).Render();
                }

                var pendingMl = KilnManager.DisplayPendingMl(state);
                var inputId = state.Contents.PendingInputId;
                if (pendingMl > 0f && inputId != 0)
                {
                    Mesh? lump;
                    if (ItemRegistry.IsItem(inputId))
                    {
                        if (itemMeshes.TryGet(inputId, out lump, out var lumpTexture))
                        {
                            BindUnit(0, TextureTarget.Texture2DArray, lumpTexture);
                            atlasBound = false;
                        }
                    }
                    else
                    {
                        if (!atlasBound)
                        {
                            BindWorldTextures();
                            atlasBound = true;
                        }

                        lump = blockCubes.Get(inputId);
                    }

                    if (lump != null)
                    {
                        // One piece of the last thing charged in, resting over the rim and facing
                        // the opening; it shrinks as its last unit melts into the pool below
                        var size = KilnRenderer.PendingSize
                                 * KilnRenderer.PendingScale01(pendingMl, KilnManager.PendingUnitMl(state));
                        var foot = KilnRenderer.ToWorld(anchor, facing, KilnRenderer.PendingLocal);
                        var placed = Matrix4x4.CreateScale(size)
                                    * Matrix4x4.CreateRotationY(KilnRenderer.OpeningYaw(facing))
                                    * Matrix4x4.CreateTranslation(foot + new Vector3(0f, size * 0.5f, 0f));
                        SetDrawUniforms(placed, placed * viewProj, 1f);
                        lump.Render();
                    }
                }
            }

            if (state.Fuel <= 0 || state.FuelItemId == 0) continue;

            var fuelIsItem = ItemRegistry.IsItem(state.FuelItemId);
            Mesh? fuelMesh;
            if (fuelIsItem)
            {
                if (!itemMeshes.TryGet(state.FuelItemId, out fuelMesh, out var fuelTexture)) continue;
                BindUnit(0, TextureTarget.Texture2DArray, fuelTexture);
                atlasBound = false;
            }
            else
            {
                if (!atlasBound)
                {
                    BindWorldTextures();
                    atlasBound = true;
                }
                fuelMesh = blockCubes.Get(state.FuelItemId);
                if (fuelMesh == null) continue;
            }

            // Spread the stored units across the back of the hearth; sprites lie face-up on the
            // floor, cubes stand on it, both turned to the opening.
            var along = MultiblockShape.RotateDirection(Vector3.UnitX, facing);
            var yaw = KilnRenderer.OpeningYaw(facing);
            var orient = fuelIsItem
                ? SpritePose.LyingFaceUp(yaw)
                : Matrix4x4.CreateRotationY(yaw);
            var lift = fuelIsItem
                ? SpritePose.RestingLift(KilnRenderer.FuelSize)
                : KilnRenderer.FuelSize * 0.5f;
            var basePos = KilnRenderer.ToWorld(anchor, facing, KilnRenderer.FuelLocal)
                        + new Vector3(0f, lift, 0f);
            for (var i = 0; i < state.Fuel; i++)
            {
                var placed = Matrix4x4.CreateScale(KilnRenderer.FuelSize)
                            * orient
                            * Matrix4x4.CreateTranslation(
                                basePos + along * ((i - (state.Fuel - 1) * 0.5f) * KilnRenderer.FuelSpacing));
                SetDrawUniforms(placed, placed * viewProj, 1f);
                fuelMesh.Render();
            }
        }
    }

    /// <summary>
    /// Draws what sits in a mold's pocket: the molten pool rising as it is poured, then
    /// dissolving into the finished cast's sprite as it cools. The two alphas come from
    /// <see cref="CastingRenderer"/> and always sum to 1, so the cross-fade reads as one
    /// dissolve over the whole cooling time rather than a cut. The liquid is drawn after the
    /// sprite because it lies above it.
    /// </summary>
    public void RenderCastings(Camera camera, CastingManager castings)
    {
        if (camera == null || castings == null || !castings.HasAny) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();

        foreach (var (cell, materialId, fill01, cool01, done, resultId) in castings.CastingView())
        {
            if (done || cool01 > 0f)
            {
                Mesh? mesh;
                Matrix4x4 sprite;
                if (ItemRegistry.IsItem(resultId))
                {
                    if (!itemMeshes.TryGet(resultId, out mesh, out var resultTexture)) continue;
                    BindUnit(0, TextureTarget.Texture2DArray, resultTexture);

                    // The cast lies face-up in the pocket, filling it
                    sprite = Matrix4x4.CreateScale(CastingRenderer.ResultSize)
                            * SpritePose.LyingFaceUp(0f)
                            * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f,
                                CastingRenderer.ResultY, 0.5f));
                }
                else
                {
                    mesh = blockCubes.Get(resultId);
                    if (mesh == null) continue;

                    // A block result is a cube fitted to the pocket's depth
                    sprite = Matrix4x4.CreateScale(CastingRenderer.ResultCubeSize)
                            * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f,
                                CastingRenderer.ResultCubeY, 0.5f));
                }

                SetDrawUniforms(sprite, sprite * viewProj, done ? 1f : CastingRenderer.ResultAlpha(cool01));
                mesh.Render();
                BindWorldTextures();
            }

            if (done || fill01 <= 0f) continue;
            if (ContentRegistry.MoltenMaterialById(materialId) is not { } material) continue;

            var liquid = Matrix4x4.CreateScale(CastingRenderer.PocketWidth)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f,
                            CastingRenderer.LiquidHeight(fill01), 0.5f));
            SetDrawUniforms(liquid, liquid * viewProj, CastingRenderer.LiquidAlpha(cool01));
            MoltenQuad(material).Render();
        }

        // Leave the shared alpha uniform where every other entity pass expects it
        worldShader.SetUniform(_locAlpha, 1f);
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
    /// Draws the live slimes from the shared entity mesh, each squashed/stretched and yawed by
    /// its own state and textured with its own skin. Restores the atlas on unit 0 on the way out
    /// so the passes that follow (the water and cloud passes) see the world textures again.
    /// </summary>
    public void RenderSlimes(Camera camera, IReadOnlyList<Slime> slimes)
    {
        if (camera == null || _slimeMesh == null || slimes.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();
        BindUnit(0, TextureTarget.Texture2DArray, _slimeTexture);

        var viewProj = camera.GetViewProjection();
        var drewFlash = false;

        foreach (var slime in slimes)
        {
            var model = SlimeTransform(slime);
            SetDrawUniforms(model, model * viewProj, 1f);
            worldShader.SetUniform(_locHitFlash, slime.HitFlash);
            _slimeMesh.Render();
            drewFlash = true;
        }

        // uHitFlash is a per-slime value, so put it back rather than tinting whatever draws next.
        if (drewFlash)
            worldShader.SetUniform(_locHitFlash, 0f);

        BindWorldTextures();
    }

    // The model's base sits on y = 0 while Position is the body centre, so the feet offset lifts
    // it onto the ground; scaling about the origin then squashes it from its base, not its middle.
    private static Matrix4x4 SlimeTransform(Slime slime) =>
        Matrix4x4.CreateScale(slime.HorizontalScale, slime.VerticalScale, slime.HorizontalScale)
        * Matrix4x4.CreateRotationY(slime.Facing)
        * Matrix4x4.CreateTranslation(
            slime.Position.X, slime.Position.Y - Slime.HalfExtent, slime.Position.Z);

    /// <summary>
    /// Draws the five-heart healthbar above every live slime (see
    /// <see cref="EntityHealthbarRenderer"/>), right after the bodies so the row tracks them.
    /// </summary>
    public void RenderEntityHealthbars(Camera camera, IReadOnlyList<Slime> slimes)
    {
        if (camera == null || slimes.Count == 0 || _healthbars == null) return;

        _healthbars.Render(slimes, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            _fogColor, _fogNear, _fogFar);
        // The healthbar renderer binds its own heart sprites to unit 0.
        InvalidateTextureUnits();
    }

    /// <summary>
    /// Draws every live falling leaf in one batched billboard draw. Leaves are too small to
    /// cast shadows, so they skip the shadow pass; their own shader carries the frame light
    /// and fog, which is final by the time the entity pass runs.
    /// </summary>
    public void RenderLeafParticles(Camera camera, IReadOnlyList<LeafParticle> leaves)
    {
        if (camera == null || leaves.Count == 0 || _leaves == null) return;

        _leaves.Render(leaves, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            textureAtlas, TextureAtlas.TileWidth, _sunDir, _sunColor, _sunIntensity,
            _ambient, _fogColor, _fogNear, _fogFar);
        // LeafParticleRenderer binds unit 0 itself. It happens to be the same atlas today, but
        // the renderer does not promise that.
        InvalidateTextureUnits();
    }

    public void RenderFireParticles(Camera camera, IReadOnlyList<FireParticle> fire)
    {
        if (camera == null || fire.Count == 0 || _fire == null) return;

        _fire.Render(fire, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            _fogColor, _fogNear, _fogFar);
    }

    public void RenderBubbleParticles(Camera camera, IReadOnlyList<BubbleParticle> bubbles)
    {
        if (camera == null || bubbles.Count == 0 || _bubbles == null) return;

        _bubbles.Render(bubbles, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            _fogColor, _fogNear, _fogFar);
        // BubbleParticleRenderer binds its own sprite to unit 0.
        InvalidateTextureUnits();
    }

    /// <summary>
    /// Draws the live mining debris as small cubes textured as the block they broke off
    /// from. Each cube spins and tumbles about its centre and fades through the world
    /// shader's alpha, like the item drops but smaller and never collected.
    /// </summary>
    public void RenderBlockDebris(Camera camera, IReadOnlyList<BlockDebris> debris)
    {
        if (camera == null || debris.Count == 0) return;

        worldShader.Use();
        BindWorldTextures();

        var viewProj = camera.GetViewProjection();

        foreach (var cube in debris)
        {
            var mesh = blockCubes.Get(cube.BlockId);
            if (mesh == null) continue;

            var model = Matrix4x4.CreateScale(cube.Size)
                        * Matrix4x4.CreateRotationX(cube.Tumble)
                        * Matrix4x4.CreateRotationY(cube.Spin)
                        * Matrix4x4.CreateTranslation(cube.Position);

            SetDrawUniforms(model, model * viewProj, MathUtils.Clamp01(cube.Alpha));
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws the player body at the camera's feet, facing with the camera yaw. First person
    /// draws everything but the head, shifted slightly back so the eye sits forward of the
    /// chest; shoulder-surf draws the full body (head included) unshifted at the true feet.
    /// The F5 glide uses one opaque shell sliding between those offsets while the head fades
    /// in on top, so no two shells ever coexist to z-fight. The shadow pass always draws the
    /// same body unshifted, head included.
    /// </summary>
    public void RenderPlayer(Camera camera, PlayerModel player)
    {
        if (camera == null || player == null) return;

        worldShader.Use();
        BindWorldTextures();

        BindUnit(0, TextureTarget.Texture2DArray, _playerPaletteTex);

        var blend = camera.ShoulderBlend;
        var viewProj = camera.GetViewProjection();
        var bodyYaw = camera.PlayerYaw;
        if (blend <= 0f)
        {
            foreach (var part in PlayerModel.FirstPersonParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch,
                    PlayerModel.FirstPersonBodyBack);
                SetDrawUniforms(model, model * viewProj, 1f);
                _playerMeshes[part].Render();
            }
        }
        else if (blend >= 1f)
        {
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch);
                SetDrawUniforms(model, model * viewProj, 1f);
                _playerMeshes[part].Render();
            }
            var head = player.GetPartMatrix("Head", camera.Position, bodyYaw, camera.Pitch);
            SetDrawUniforms(head, head * viewProj, 1f);
            _playerMeshes["Head"].Render();
        }
        else
        {
            var back = PlayerModel.FirstPersonBodyBack * (1f - blend);
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch, back);
                SetDrawUniforms(model, model * viewProj, 1f);
                _playerMeshes[part].Render();
            }
            gl.DepthMask(false);
            var head = player.GetPartMatrix("Head", camera.Position, bodyYaw, camera.Pitch);
            SetDrawUniforms(head, head * viewProj, blend);
            _playerMeshes["Head"].Render();
            gl.DepthMask(true);
        }

        BindWorldTextures();
    }

    // Held-item sizing, in blocks. The sprite is unit-sized and the cube is full-block;
    // both are shrunk to sit in the palm. The grips lift the shape just clear of the
    // forearm with a nudge outward (+X, away from the body) and forward (-Z).
    // Public so the item debugger can show and tune the same defaults the game ships.
    public const float HeldItemScale = 0.5f;
    public static readonly Vector3 HeldItemGrip = new(0.03f, 0.10f, -0.04f);
    public const float HeldBlockScale = 0.38f;
    public static readonly Vector3 HeldBlockGrip = new(0.08f, 0.10f, -0.02f);

    /// <summary>
    /// The held-item model matrix: uniform scale, the item's display transform, the grip nudge,
    /// then the hand anchor. Shared by <see cref="RenderHeldItem"/> and the item debugger so the
    /// preview can never drift from what the game draws. Blocks pass
    /// <see cref="Matrix4x4.Identity"/> as <paramref name="displayTransform"/>: their shape is
    /// the block's own baked model, not a display-configured sprite.
    /// </summary>
    public static Matrix4x4 PoseHeldItem(Matrix4x4 hand, Matrix4x4 displayTransform, float scale, Vector3 grip) =>
        Matrix4x4.CreateScale(scale) * displayTransform * Matrix4x4.CreateTranslation(grip) * hand;

    /// <summary>
    /// Draws the selected hotbar slot in the right hand: a block as its 3D cube, a pure
    /// item as its sprite posed by the item's display transform (canted tools by default,
    /// the compass facing back up at the holder). Positioned at the fist through the hand
    /// anchor, so it rides the carrying pose, the walk swing and the punch.
    /// </summary>
    public void RenderHeldItem(Camera camera, PlayerModel player, byte selectedId, bool hasHeld,
        float timeSeconds, float headingDegrees)
    {
        if (camera == null || player == null || selectedId == 0 || !hasHeld) return;

        var backOffset = PlayerModel.FirstPersonBodyBack * (1f - camera.ShoulderBlend);
        var hand = player.GetHandAnchorMatrix(camera.Position, camera.PlayerYaw, backOffset,
            camera.Pitch);
        var viewProj = camera.GetViewProjection();

        worldShader.Use();
        BindWorldTextures();

        if (ItemRegistry.IsItem(selectedId))
        {
            if (ItemRegistry.TryGet(selectedId) is not { } type) return;
            var frame = Math.Clamp(type.FrameAt(timeSeconds, headingDegrees), 0,
                Math.Max(0, type.FrameCount - 1));
            if (!itemMeshes.TryGetFrame(selectedId, frame, out var mesh, out var texture)) return;

            var model = PoseHeldItem(hand, type.DisplayMatrix(), HeldItemScale, HeldItemGrip);
            SetDrawUniforms(model, model * viewProj, 1f);

            BindUnit(0, TextureTarget.Texture2DArray, texture);
            mesh.Render();
            BindWorldTextures();
            return;
        }

        var cube = blockCubes.Get(selectedId);
        if (cube == null) return;
        var blockModel = PoseHeldItem(hand, Matrix4x4.Identity, HeldBlockScale, HeldBlockGrip);
        SetDrawUniforms(blockModel, blockModel * viewProj, 1f);
        cube.Render();
    }

    private void DrawPlayerPartShadow(PlayerModel player, string part, Camera camera)
    {
        var model = player.GetPartMatrix(part, camera.Position, camera.PlayerYaw, camera.Pitch);
        shadowShader.SetUniform("uModel", model);
        shadowShader.SetUniform("uLightMVP", model * _lightViewProj);
        _playerMeshes[part].Render();
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
    /// Draws the targeted block's outline and face highlight. Each of the block's hitboxes gets
    /// its own outline; the highlight lands on the hit box's face. <paramref name="grow"/> is the
    /// block's current pop growth so the highlight tracks it while mining.
    /// </summary>
    public void RenderBlockSelection(Camera camera, Vector3 blockPosition, Vector3 hitNormal, float grow = 0f,
        byte blockId = 0, int hitBoxIndex = 0, BlockHitbox[]? boxes = null)
    {
        selectionShader.Use();

        var viewProj = camera.GetViewProjection();
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        boxes ??= BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        selectionShader.SetUniform("uColor", new Vector3(0.8f, 0.8f, 0.85f));
        selectionShader.SetUniform("uOpacity", 0.95f);

        gl.LineWidth(2f);
        gl.BindVertexArray(outlineVao);
        // The outline inflates a hair past the grown box so it never z-fights the block
        var outlinePad = new Vector3(0.01f + grow);
        foreach (var box in boxes)
        {
            var boxCenter = blockPosition + box.Center;
            var outlineModel = Matrix4x4.CreateScale(box.Size + outlinePad) * Matrix4x4.CreateTranslation(boxCenter);
            selectionShader.SetUniform("uMVP", outlineModel * viewProj);
            unsafe { gl.DrawElements(PrimitiveType.Lines, 24, DrawElementsType.UnsignedInt, null); }
        }

        var hitBox = boxes[Math.Clamp(hitBoxIndex, 0, boxes.Length - 1)];
        var faceIndex = FaceIndexFromNormal(hitNormal);
        if (faceIndex >= 0)
        {
            var hitCenter = blockPosition + hitBox.Center;
            var faceModel = Matrix4x4.CreateTranslation(new Vector3(-0.5f))
                            * Matrix4x4.CreateScale(hitBox.Size + new Vector3(grow))
                            * Matrix4x4.CreateTranslation(hitCenter + hitNormal * 0.003f);
            selectionShader.SetUniform("uMVP", faceModel * viewProj);
            selectionShader.SetUniform("uColor", new Vector3(0.7f, 0.7f, 0.75f));
            selectionShader.SetUniform("uOpacity", 0.15f);

            gl.BindVertexArray(faceVao);
            gl.DrawArrays(GLEnum.TriangleFan, faceIndex * 4, 4);
        }

        gl.BindVertexArray(0);
        gl.DepthMask(true);
    }

    /// <summary>
    /// Draws the block-breaking crack overlay on every face of each of the block's hitboxes:
    /// the sprite steps through <see cref="WorldGenerationConfig.BreakOverlayFrames"/> stacked
    /// frames as progress goes 0 -> 1, the first frame being blank (see break-fragment.glsl).
    /// <paramref name="grow"/> is the block's current pop growth so the overlay tracks it while
    /// mining.
    /// </summary>
    public void RenderBlockBreaking(Camera camera, Vector3 blockPosition, float progress, float grow = 0f,
        byte blockId = 0, BlockHitbox[]? boxes = null)
    {
        if (progress <= 0f) return;

        breakShader.Use();
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        boxes ??= BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        var frame = Math.Clamp((int)(progress * WorldGenerationConfig.BreakOverlayFrames), 0,
            WorldGenerationConfig.BreakOverlayFrames - 1);
        breakShader.SetUniform("uFrame", (float)frame);
        BindUnit(0, TextureTarget.Texture2DArray, _breakProgressTexture);

        gl.BindVertexArray(faceVao);
        // Inflated slightly past the grown box so the overlay never z-fights the block.
        // UVs are cropped to the face's cell-space footprint so thin boxes sample a strip
        // of the crack sprite at full-texel density instead of a squashed whole frame.
        var pad = new Vector3(0.004f + grow);
        var viewProj = camera.GetViewProjection();
        foreach (var box in boxes)
        {
            var model = Matrix4x4.CreateTranslation(new Vector3(-0.5f))
                        * Matrix4x4.CreateScale(box.Size + pad)
                        * Matrix4x4.CreateTranslation(blockPosition + box.Center);
            breakShader.SetUniform("uMVP", model * viewProj);
            for (var face = 0; face < 6; face++)
            {
                var (offset, scale) = BreakOverlayUv.ForFace(face, box);
                breakShader.SetUniform("uUvOffset", offset);
                breakShader.SetUniform("uUvScale", scale);
                gl.DrawArrays(GLEnum.TriangleFan, face * 4, 4);
            }
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
            // Cube faces in Chunk.CubeFaces order, each a 4-vertex triangle fan, interleaved as
            // position.xyz + face-local uv. The two in-plane axes carry uv 0..1; the break
            // shader crops them to the hitbox face via uUvOffset/uUvScale, so a full cube
            // samples the whole sprite frame and a thin box a strip of it.
            0f, 0f, 0f, 0f, 0f,   1f, 0f, 0f, 1f, 0f,
            1f, 1f, 0f, 1f, 1f,   0f, 1f, 0f, 0f, 1f,
            1f, 0f, 1f, 1f, 0f,   0f, 0f, 1f, 0f, 0f,
            0f, 1f, 1f, 0f, 1f,   1f, 1f, 1f, 1f, 1f,
            0f, 0f, 1f, 1f, 0f,   0f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f, 1f,   0f, 1f, 1f, 1f, 1f,
            1f, 0f, 0f, 0f, 0f,   1f, 0f, 1f, 1f, 0f,
            1f, 1f, 1f, 1f, 1f,   1f, 1f, 0f, 0f, 1f,
            0f, 1f, 0f, 0f, 0f,   0f, 1f, 1f, 0f, 1f,
            1f, 1f, 1f, 1f, 1f,   1f, 1f, 0f, 1f, 0f,
            0f, 0f, 1f, 0f, 1f,   1f, 0f, 1f, 1f, 1f,
            1f, 0f, 0f, 1f, 0f,   0f, 0f, 0f, 0f, 0f
        ];

        faceVao = gl.GenVertexArray();
        faceVbo = gl.GenBuffer();
        gl.BindVertexArray(faceVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, faceVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, faceVertices, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float),
                (void*)(3 * sizeof(float)));
        }
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// The mining pop's streaming buffers: a dynamic VBO refilled with the hitbox boxes every
    /// frame while mining, and a static EBO holding the box-quad index pattern (every box emits
    /// the same six quads, so any box count draws as a prefix). The pattern is generated by
    /// running the real emitter over dummy boxes, so it can never drift from what
    /// <see cref="RenderBlockPop"/> streams.
    /// </summary>
    private void CreatePopGeometry()
    {
        _popVao = gl.GenVertexArray();
        _popVbo = gl.GenBuffer();
        _popEbo = gl.GenBuffer();

        gl.BindVertexArray(_popVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _popVbo);
        gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer,
            (nuint)(_popBoxCapacity * 24 * Vertex.Size),
            new Vertex[_popBoxCapacity * 24], BufferUsageARB.DynamicDraw);

        var dummyVerts = new List<Vertex>(_popBoxCapacity * 24);
        var dummyIndices = new List<ushort>(_popBoxCapacity * 36);
        ushort offset = 0;
        for (var i = 0; i < _popBoxCapacity; i++)
            PopBoxEmitter.EmitBox(dummyVerts, dummyIndices, ref offset,
                BlockHitbox.FullCube, BlockHitbox.FullCube, Vector3.Zero, new ushort[6]);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _popEbo);
        gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer,
            (nuint)(dummyIndices.Count * sizeof(ushort)),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(dummyIndices),
            BufferUsageARB.StaticDraw);

        Mesh.ConfigureVertexAttribs(gl);
        gl.BindVertexArray(0);
    }

    private void GrowPopBuffers(int boxes)
    {
        gl.DeleteBuffer(_popVbo);
        gl.DeleteBuffer(_popEbo);
        gl.DeleteVertexArray(_popVao);
        if (materialSsbo != 0) gl.DeleteBuffer(materialSsbo);
        _popBoxCapacity = boxes * 2;
        CreatePopGeometry();
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

    public void RenderUI(IWindow window, Inventory inventory, InventoryView view, float time,
        int fps, bool showProfiler, Camera? camera = null, bool showCoordinates = true,
        PauseMenuView? menu = null, SettingsMenuView? settings = null,
        int health = PlayerHealth.MaxHealth, float damageFlash = 0f)
    {
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        ui.Begin(window.Size, window.FramebufferSize, UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));

        // Cheap underwater wash over the whole view: the world fog alone leaves nearby
        // geometry untinted, and this is what sells "the camera is in the water"
        if (_underwaterT > 0.001f)
        {
            var wet = MathUtils.SmoothStep(_underwaterT);
            var tint = _eyeInLava ? LavaFogColor : UnderwaterFogColor;
            var alpha = (_eyeInLava ? 0.35f : 0.18f) * wet;
            ui.DrawRect(ui.ScreenCentre, ui.ScreenSize,
                Rgba.FromFloat(tint.X, tint.Y, tint.Z, alpha));
        }

        if (damageFlash > 0.001f)
            _vignette?.Render(window.Size, damageFlash, time);

        if (!view.IsOpen && (menu == null || !menu.IsOpen))
            ui.DrawCrosshair();
        if (camera != null && HasCompassEquipped(inventory))
            ui.DrawCompass(camera.Yaw, camera.Position, showCoordinates);
        ui.DrawFps(fps);
        if (showProfiler)
            ui.DrawProfiler(FrameProfiler.Lines);
        var heading = camera != null ? Compass.HeadingFromYaw(camera.Yaw) : 0f;
        ui.DrawHotbar(inventory, heading, time);
        if (!view.IsOpen && (menu == null || !menu.IsOpen))
            ui.DrawHearts(health);
        if (view.IsOpen)
            ui.DrawInventoryPanel(inventory, view, time, heading);
        if (menu != null && settings != null)
            ui.DrawPauseMenu(menu, settings);
        ui.End();
        // The font and the item icons take unit 0, and this pass sits across the frame boundary.
        InvalidateTextureUnits();
    }

    /// <summary>Delegates click hit-testing to the UI pass (valid after a frame has begun).</summary>
    public int? UiHotbarSlotAt(Vector2 point) => ui.HotbarSlotAt(point);
    /// <summary>Window pixels -> UI canvas units (the space every UI layout is measured in).</summary>
    public Vector2 UiToCanvas(Vector2 windowPoint) => ui.ToCanvas(windowPoint);
    /// <summary>The UI canvas size of the last UI pass, in canvas units.</summary>
    public Vector2 UiCanvasSize => ui.ScreenSize;
    public InventoryCategory? UiPanelTabAt(Vector2 point) => ui.PanelTabAt(point);
    public int? UiPanelRowAt(Vector2 point, float scrollOffset, int rowCount) =>
        ui.PanelRowAt(point, scrollOffset, rowCount);
    public CraftHit UiCraftHitAt(Vector2 point, InventoryView view) => ui.CraftHitAt(point, view);
    public bool UiIsOverCraftCard(Vector2 point) => ui.IsOverCraftCard(point);
    public float UiClampCraftScroll(float offset) => ui.ClampCraftScroll(offset);
    public PauseMenuHit UiPauseMenuHitAt(Vector2 point, PauseMenuView menu) => ui.PauseMenuHitAt(point, menu);
    public SettingsHit UiSettingsHitAt(Vector2 point, SettingsMenuView settings) => ui.SettingsHitAt(point, settings);

    // The compass item id, resolved once: null when no item named "compass" is registered,
    // in which case the HUD compass never shows rather than throwing every frame.
    private byte? _compassId;
    private bool _compassResolved;

    private bool HasCompassEquipped(Inventory inventory)
    {
        if (!_compassResolved)
        {
            _compassResolved = true;
            try
            {
                _compassId = ContentRegistry.RequireItem("compass").Id;
            }
            catch (InvalidOperationException)
            {
                _compassId = null;
            }
        }
        return _compassId.HasValue && inventory.ContainsInHotbar(_compassId.Value);
    }
    public void Dispose()
    {
        // R6's counted instrument, and the reason it is a shutdown log line rather than a token on
        // FrameProfiler's console row: Program.LogDebugLine returns early when debug output is off,
        // and that is the very flag the gate reads, so a console token could only ever report the
        // always-on configuration. This prints in every run, gated or not — the constructor's one
        // "GPU:" line is the precedent — and Program.OnClosing disposes before Log.Shutdown.
        Log.WriteLine($"GPU timer queries: {_gpuQueryCalls} GL calls over {_gpuFrameIndex} frames, " +
                      $"{_gpuTimedFrames} timed ({(_gpuFrameIndex > 0 ? _gpuQueryCalls / (double)_gpuFrameIndex : 0):F1} per frame)");

        ui.Dispose();
        blockCubes.Dispose();
        itemMeshes.Dispose();
        _quernTop?.Dispose();
        _crucibleMesh?.Dispose();
        foreach (var quad in _moltenQuads.Values)
            quad.Dispose();
        _moltenQuads.Clear();
        foreach (var mesh in _playerMeshes.Values)
            mesh.Dispose();
        _playerMeshes.Clear();
        gl.DeleteTexture(_playerPaletteTex);
        _slimeMesh?.Dispose();
        if (_slimeTexture != 0)
            gl.DeleteTexture(_slimeTexture);
        worldShader.Dispose();
        chunkShader.Dispose();
        selectionShader.Dispose();
        breakShader.Dispose();
        shadowShader.Dispose();
        chunkShadowShader.Dispose();
        sunShader.Dispose();
        _clouds?.Dispose();
        _rays?.Dispose();
        _vignette?.Dispose();
        _leaves?.Dispose();
        _fire?.Dispose();
        _bubbles?.Dispose();
        _healthbars?.Dispose();
        _sunQuad.Dispose();
        gl.DeleteTexture(_sunTexture);
        gl.DeleteTexture(_breakProgressTexture);
        gl.DeleteFramebuffer(_shadowFbo);
        gl.DeleteTexture(_shadowDepthTex);
        DeleteReflectionTarget();
        DeleteSceneCopy();
        DeleteRaysTarget();
        gl.DeleteBuffer(outlineVbo);
        gl.DeleteBuffer(outlineEbo);
        gl.DeleteVertexArray(outlineVao);
        gl.DeleteBuffer(faceVbo);
        gl.DeleteVertexArray(faceVao);
        gl.DeleteBuffer(_popVbo);
        gl.DeleteBuffer(_popEbo);
        gl.DeleteVertexArray(_popVao);
        foreach (var query in _gpuPassQueries)
            gl.DeleteQuery(query);
        // The chunks and LOD regions that hold slices in these pools are gone by now (the world
        // stops before the render manager does), so freeing the buffers here cannot orphan a
        // live slice.
        _chunkDraws.Dispose();
        OpaqueChunkGeometry.Dispose();
        TranslucentChunkGeometry.Dispose();
        LodGeometry.Dispose();
        gl.Dispose();
    }
}
