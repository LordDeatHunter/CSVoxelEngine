using System;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
using Silk.NET.Maths;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Effects.Water;
using VoxelEngine.Graphics;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Actors;
using VoxelEngine.Rendering.Atmosphere;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Rendering.Overlays;
using VoxelEngine.Rendering.ScreenSpace;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering;

/// <summary>
/// The composition root of the frame's GL state. It creates every GL resource the world and the UI
/// draw with and frees it again, each in one fixed order (<see cref="LoadResources"/>,
/// <see cref="Dispose"/>); owns the world programs with their one-time
/// (<see cref="InitWorldProgram"/>) and per-frame (<see cref="ApplyFrameUniforms"/>,
/// <see cref="SetDrawUniforms"/>) uniforms and the shared resources: the atlases, the block and
/// item caches, the chunk and LOD geometry pools, the chunk draw batch, the shadow cascades and
/// the sky and particle renderers. The passes that draw with them are the components it builds,
/// exposed as internal properties (<see cref="Terrain"/>, <see cref="Lighting"/>, ...) and called
/// directly by <see cref="WorldRenderer"/> and Program. It also carries the static facade the item
/// debugger and the tests read. Render thread only.
/// </summary>
public class RenderManager : IDisposable
{
    public readonly GL gl;
    private Shader worldShader;
    private Shader selectionShader;
    private Shader breakShader;
    private Shader shadowShader;
    private Shader sunShader;
    private Shader moonShader;

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

    /// <summary>Texture unit of the far shadow cascade's depth map. Units 0-4 are the tracked world
    /// set, 5 is the underwater-rays detour and 6 the material atlas; 7 is untracked like 6, so
    /// <see cref="TextureUnits.BindLazy"/> always rebinds it.</summary>
    internal const int FarShadowUnit = 7;

    /// <summary>Texture unit of the cloud-shadow coverage map (<see cref="CloudShadowTexture"/>).
    /// Units 0-4 are the tracked world set, 5 the underwater-rays detour, 6 the material atlas and
    /// 7 the far cascade. Unit 8 is bound ONCE in <see cref="LoadResources"/> and nothing else ever
    /// binds it, so <see cref="BindWorldTextures"/> does not rebind it; any future pass that binds a
    /// texture here must restore it or the cloud shadow silently samples the wrong texture.</summary>
    internal const int CloudShadowUnit = 8;

    /// <summary>Transient unit the display-space mixes read the target they draw into from: the
    /// selection face highlight (<see cref="SelectionPasses.RenderBlockSelection"/>) and the cloud layer
    /// (<see cref="SkyPasses.RenderClouds"/> and the mirror's clouds); bound only around those draws and
    /// unbound again.</summary>
    internal const int SceneReadUnit = 9;

    /// <summary>Texture units of the opaque pass's occlusion cull (<see cref="ChunkOcclusionCuller"/>):
    /// the scene depth while its pyramid is built (unbound again straight after) and the pyramid itself.
    /// Nothing else binds them.</summary>
    internal const int HizDepthUnit = 10, HizPyramidUnit = 11;
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
    /// (1024 MB vs 64 MB), not a correctness one, and does not transfer. See
    /// <see cref="ChunkGeometryPool.LodInitialUnits"/> for the sizing derivation.
    /// </summary>
    public ChunkGeometryPool LodGeometry { get; private set; }

    // The persistent-mapped indirect-command ring + ChunkInfo SSBO the chunk passes fill and
    // draw with one MultiDrawElementsIndirect each.
    private ChunkDrawBatch _chunkDraws;
    private UIRenderer ui;
    private BlockCubeCache blockCubes;
    private ItemMeshCache itemMeshes;

    // The item-debugger and test facade over the passes' statics: the item debugger loads the same
    // player model and poses its preview with the same held-item numbers, and the tests place the
    // fire light with the same offset.
    /// <summary>Re-exports <see cref="PlayerPasses.PlayerModelPath"/> for the item debugger.</summary>
    public const string PlayerModelPath = PlayerPasses.PlayerModelPath;
    /// <summary>Re-exports <see cref="HeldItemPose.HeldItemScale"/> for the item debugger and its tests.</summary>
    public const float HeldItemScale = HeldItemPose.HeldItemScale;
    /// <summary>Re-exports <see cref="HeldItemPose.HeldItemGrip"/> for the item debugger and its tests.</summary>
    public static Vector3 HeldItemGrip => HeldItemPose.HeldItemGrip;
    /// <summary>Re-exports <see cref="HeldItemPose.HeldBlockScale"/> for the item debugger and its tests.</summary>
    public const float HeldBlockScale = HeldItemPose.HeldBlockScale;
    /// <summary>Re-exports <see cref="HeldItemPose.HeldBlockGrip"/> for the item debugger and its tests.</summary>
    public static Vector3 HeldBlockGrip => HeldItemPose.HeldBlockGrip;
    /// <summary>Re-exports <see cref="HeldItemPose.Pose"/> for the item debugger, whose preview
    /// poses the held item exactly as the game draws it.</summary>
    public static Matrix4x4 PoseHeldItem(Matrix4x4 hand, Matrix4x4 displayTransform, float scale,
        Vector3 grip) => HeldItemPose.Pose(hand, displayTransform, scale, grip);
    /// <summary>Re-exports <see cref="FrameLighting.FireLightOffset"/>, where a fire cell's warm point
    /// light sits inside it, for the tests.</summary>
    public static Vector3 FireLightOffset => FrameLighting.FireLightOffset;

    private uint textureAtlas;
    /// <summary>Index-parallel to <see cref="textureAtlas"/>: per-tile normal + metallic mask.</summary>
    private uint materialAtlas;
    /// <summary>Per-tile (metallic, roughness, normalStrength) for the world shader, at
    /// <see cref="MaterialSsboBinding"/>. Zero while no tile is reflective.</summary>
    private uint materialSsbo;

    // The sun disc: one shared unit quad plus its sprite, oriented around the sky's sun direction
    // (FrameLighting.SkySunDir) every frame by SkyPasses.RenderSun, faded by FrameLighting.SunVisibility.
    private uint _sunTexture;
    private Mesh _sunQuad;

    // The moon disc (SkyPasses.RenderMoon): the sun's quad again, opposite the sun, with its own
    // sprite and the phase (MoonPhase) shaded over it; FrameLighting.MoonVisibility fades it.
    private uint _moonTexture;

    private uint _breakProgressTexture;

    // The cloud layer as one static, stitched mesh (see CloudRenderer), drawn translucent
    // after the water pass. Null until LoadResources builds it.
    private CloudRenderer? _clouds;

    // The cloud layer's baked coverage, sampled by the world shader's CloudShadow on unit 8.
    // Null until LoadResources builds it.
    private CloudShadowTexture? _cloudShadow;

    // The sky dome, one full-screen pass drawn before the sun disc (SkyPasses.RenderSky). Null
    // until LoadResources builds it; the clear color stands in for it until then.
    private SkyRenderer? _skyDome;

    // Damage reddening: one full-screen radial-gradient pass, drawn first inside the UI pass.
    // Null until LoadResources builds it.
    private DamageVignetteRenderer? _vignette;

    // Falling leaves: one dynamic billboard batch drawn in the entity pass, after the
    // cubes. Null until LoadResources builds it.
    private LeafParticleRenderer? _leaves;
    private FireParticleRenderer? _fire;
    private BubbleParticleRenderer? _bubbles;
    private SliceTrailRenderer? _sliceTrails;

    // Entity healthbars: one five-heart billboard row above every live slime.
    // Null until LoadResources builds it.
    private EntityHealthbarRenderer? _healthbars;
    private DamageNumberRenderer? _damageNumbers;
    private NoticePipRenderer? _noticePips;

    // Directional sun shadows: ShadowPass.RenderShadows fills _nearShadow's depth texture (a
    // depth-only FBO attachment, sampled as a comparison sampler on unit 1) through its ViewProj,
    // and every world draw hands both to test-fragment.glsl's SampleShadow. The cascade also
    // carries its own texel-stepped box center (ShadowCascade.StepCenter) from frame to frame.
    private readonly ShadowCascade _nearShadow =
        new(LightingConfig.ShadowRadiusBlocks, LightingConfig.ShadowMapSize);

    // The coarse far cascade (unit 7): chunk and LOD casters only, sampled by SampleShadow where the
    // near box fades out. Its own ShadowCascade, so it steps its own center on its own texel lattice.
    // The map is cached: FrameLighting re-renders it only when FarShadowReuse asks, and its ViewProj
    // is always the matrix the map holds, so the uLightViewProjFar upload samples it correctly.
    private readonly ShadowCascade _farShadow =
        new(LightingConfig.FarShadowRadiusBlocks, LightingConfig.FarShadowMapSize);

    // The sea surface the reflection mirrors across (a source cell's top), and the
    // per-frame clip that keeps below-plane geometry out of the reflection texture.
    // _clipY rides ApplyFrameUniforms into uClipY; -1e9 disables the discard.
    internal const float WaterPlaneY = WorldGenerationConfig.SeaLevel + FluidState.SourceHeight;
    private float _clipY = -1e9f;

    // The voxel block-light colors by palette slot, already linear: copied once from
    // BlockLightPalette in LoadResources, after ContentRegistry.Load has settled it
    private Vector3[] _blockLightPalette = [];
    private const float TorchIntensity = 1.2f;
    // Average opaque color of the leaves tile, uploaded as uLodLeafColor: far LOD crowns
    // sample the cutout leaf texture stretched over whole cells, so their transparent texels
    // fall back to this instead of discarding to sky.
    private Vector3 _lodLeafColor = new(0.17f, 0.29f, 0.07f);

    /// <summary>
    /// Where entity draws read their world light (<see cref="EntityLight.Sample"/>). Set per world,
    /// once that World exists, and cleared by <see cref="ForgetWorld"/> when it is torn down; null
    /// lights every draw at <see cref="EntityLightSample.Default"/>.
    /// </summary>
    public ChunkManager? LightSource { get; set; }

    /// <summary>
    /// Drops every reference this renderer and its passes hold into the current world: the light
    /// source, the per-frame chunk and region draw lists (<see cref="TerrainPasses.ForgetWorld"/>),
    /// the mirror's entity lists and the mirror itself, marked invalid so the next world renders a
    /// fresh one instead of reusing the old image (<see cref="ReflectionPass.ForgetWorld"/>), and the
    /// eased eye state (<see cref="FrameLighting.ForgetWorld"/>).
    /// Called by <c>World.SaveAndDispose</c> after the world freed its slices. The geometry pools,
    /// programs, caches and the <see cref="ChunkDrawBatch"/> stay alive for the next world, so a
    /// frame rendered before that world exists draws nothing stale. The logged unit counts are
    /// the leak check: after a clean teardown every pool reads 0. Render thread only.
    /// </summary>
    public void ForgetWorld()
    {
        LightSource = null;
        Terrain.ForgetWorld();
        Reflection.ForgetWorld();
        Lighting.ForgetWorld();

        Log.WriteLine($"World released: geometry in use opaque {OpaqueChunkGeometry.UsedUnits}, " +
                      $"translucent {TranslucentChunkGeometry.UsedUnits}, lod {LodGeometry.UsedUnits} units");
    }

    // The eye's depth below its own water surface (Camera.EyeWaterSurfaceY), read in BeginFrame for
    // the deep-water darkness (DeepWaterFog); 0 while the eye is dry or in lava
    private float _eyeWaterDepth;

    // Hemispheric ambient: the sRGB-authored tint the sky ambient is multiplied by for a face
    // looking straight down (warmer and darker than the sky - light bounced off the ground).
    // Kept mild: ambient is all a shaded face gets, so a deep tint (0.62, 0.52, 0.42) turned
    // canopy undersides, overhangs and trunks murky next to the flat-ambient 28198cc look.
    private static readonly Vector3 GroundBounceTint = new(0.86f, 0.82f, 0.76f);
    private static readonly Vector3 GroundBounceTintLinear = ColorSpace.SrgbToLinear(GroundBounceTint);

    /// <summary>
    /// The ambient color a downward face sees, derived from the final sky ambient so the
    /// underwater and lava tints carry over to the ground bounce.
    /// </summary>
    public static Vector3 GroundAmbientOf(Vector3 skyAmbient) => skyAmbient * GroundBounceTintLinear;

    // World-shader uniform locations, resolved once in LoadResources. The first four are the
    // only uniforms a world draw still touches, so the per-draw cost is three (four for TNT,
    // five for a flashing slime) glUniform calls with no dictionary lookup in between; the arrays feed ApplyFrameUniforms.
    private int _locModel;
    private int _locMvp;
    private int _locAlpha;
    // uBiomeTint: the per-draw tint of a mining pop or a debris cube of a biome-tinted block.
    private int _locBiomeTint;
    private int _locEntitySun;
    private int _locEntityBlock;
    private int _locEntityTint;
    private int _locFlash;
    private int _locHitFlash;
    private int _locStatusTint;
    private int _locAlphaCutoff;
    private int[] _locPointPos = [];
    private int[] _locPointColor = [];
    private int[] _locBlockLightPalette = [];

    // The same, on the CHUNK_DRAW variants. uModel/uMVP/uAlpha do not exist there (the vertex
    // stage takes them from the SSBO), so only the view-projection, the SSBO base index and the
    // handful of frame uniforms the chunk programs still read are resolved.
    private int _locChunkViewProj;
    private int _locChunkDrawBase;
    private int _locChunkAlphaCutoff;
    private int[] _locChunkPointPos = [];
    private int[] _locChunkPointColor = [];
    private int[] _locChunkBlockLightPalette = [];

    // The pass components this renderer builds and owns; WorldRenderer, Program and the other
    // passes call them directly.
    /// <summary>The per-pass GPU timer ring every render pass brackets itself with.</summary>
    internal GpuPassTimer GpuTimer { get; }

    /// <summary>The one texture-unit binder every pass binds through; its
    /// <see cref="TextureUnitCache"/> must stay the only one.</summary>
    internal TextureUnits Units { get; }

    /// <summary>The viewport size and the render targets sized to it: the HDR scene target every
    /// world pass draws into, the water pass's scene copies and the half-res rays target.</summary>
    internal RenderTargets Targets { get; }

    /// <summary>The frame's light, sky, fog and eye state, computed once a frame by
    /// <see cref="FrameLighting.SetLighting"/> and read live by every pass.</summary>
    internal FrameLighting Lighting { get; }

    /// <summary>The sky passes: the dome, the sun and moon discs and the cloud layer.</summary>
    internal SkyPasses Sky { get; }

    /// <summary>The full-screen passes over the HDR scene: the tonemap resolve, the contact AO,
    /// the underwater sun shafts and the godrays.</summary>
    internal ScreenPasses Screen { get; private set; }

    /// <summary>The billboard draws of the entity pass: the mob healthbars, the floating damage
    /// numbers, the notice pips and the leaf, fire and bubble particles.</summary>
    internal BillboardPasses Billboards { get; }

    /// <summary>The selection pass's draws: the mining and placement pop, the selection outline
    /// with its face highlight and the crack overlay.</summary>
    internal SelectionPasses Selection { get; private set; }

    /// <summary>The station draws of the entity pass: the food on a campfire, the quern tops,
    /// what a kiln holds and the cast in a mold.</summary>
    internal StationPasses Stations { get; private set; }

    /// <summary>The loose-object draws of the entity pass: the item drops, thrown items, primed
    /// TNT, falling blocks, mining debris and toppling trees; the water mirror reuses their
    /// Draw* cores.</summary>
    internal ObjectPasses Objects { get; }

    /// <summary>The player draws: the local body, the held item and the remote players; owns
    /// the player rig, its palette texture and its part meshes.</summary>
    internal PlayerPasses Players { get; private set; }

    /// <summary>The mob draws: the slimes and the goblins; owns the slime mesh and skin and the
    /// goblin rig, its part meshes and its skin.</summary>
    internal MobPasses Mobs { get; private set; }

    /// <summary>The iceberg draws: one mesh and skin per iceberg variant.</summary>
    internal IcebergPasses IcebergPasses { get; private set; }

    /// <summary>The chest draws: the shared chest model's base and lid for every chest cell, each on its
    /// block's skin.</summary>
    internal ChestPasses Chests { get; private set; }

    /// <summary>The depth-only sun shadow passes: the near and far cascades' casters; owns the
    /// CHUNK_DRAW shadow program's uniform locations.</summary>
    internal ShadowPass Shadows { get; private set; }

    /// <summary>The terrain passes: the far LOD, the opaque chunks and the translucent chunk and
    /// LOD water draws, plus the once-a-frame water cull; the water mirror shares its scratch
    /// lists.</summary>
    internal TerrainPasses Terrain { get; }

    /// <summary>The planar water mirror: its render target, the mirrored world and entity
    /// draws and the temporal reuse that skips a re-render when nothing moved.</summary>
    internal ReflectionPass Reflection { get; }

    /// <summary>The UI hand-off: the HUD, chat, tooltip and player-list passes over the finished
    /// frame, and the world-less menu and loading frames.</summary>
    internal UiPasses UiPasses { get; }

    /// <summary>The UI pass's renderer; the input components hit-test clicks against its last layout.</summary>
    internal UIRenderer Ui => ui;

    // Live views of resources this renderer owns, for the pass components.
    internal CloudRenderer? Clouds => _clouds;
    internal CloudShadowTexture? CloudShadow => _cloudShadow;
    internal ShadowCascade NearShadow => _nearShadow;
    internal ShadowCascade FarShadow => _farShadow;

    /// <summary>
    /// Moves whenever the far shadow cascade's caster set may have changed: any opaque chunk
    /// upload or free, any LOD shell upload or free and any LOD burial flip
    /// (<see cref="ChunkGeometryPool.ContentVersion"/>). A water-only remesh also frees and
    /// re-uploads its chunk's opaque slice and so moves it, a known over-invalidation; the
    /// translucent pool never casts and is not counted. Main thread only.
    /// </summary>
    internal int FarCasterVersion => unchecked(OpaqueChunkGeometry.ContentVersion + LodGeometry.ContentVersion);
    internal SkyRenderer? SkyDome => _skyDome;
    internal Shader SunShader => sunShader;
    internal Shader MoonShader => moonShader;
    internal uint SunTexture => _sunTexture;
    internal Mesh SunQuad => _sunQuad;
    internal uint MoonTexture => _moonTexture;
    internal uint AtlasTexture => textureAtlas;
    internal LeafParticleRenderer? Leaves => _leaves;
    internal FireParticleRenderer? Fire => _fire;
    internal BubbleParticleRenderer? Bubbles => _bubbles;
    /// <summary>This frame's splash ripples for the water shader; packed before <see cref="BeginFrame"/>.</summary>
    internal RippleField WaterRipples { get; } = new();
    internal SliceTrailRenderer? SliceTrails => _sliceTrails;
    internal EntityHealthbarRenderer? Healthbars => _healthbars;
    internal DamageNumberRenderer? DamageNumbers => _damageNumbers;
    internal NoticePipRenderer? NoticePips => _noticePips;
    internal Shader WorldShader => worldShader;
    internal Shader SelectionShader => selectionShader;
    internal Shader BreakShader => breakShader;
    internal uint BreakProgressTexture => _breakProgressTexture;
    internal int LocBiomeTint => _locBiomeTint;
    internal ItemMeshCache ItemMeshes => itemMeshes;
    internal BlockCubeCache BlockCubes => blockCubes;
    internal int LocAlpha => _locAlpha;
    internal int LocFlash => _locFlash;
    internal int LocHitFlash => _locHitFlash;
    internal int LocStatusTint => _locStatusTint;
    internal Shader ShadowShader => shadowShader;
    internal Shader ChunkShadowShader => chunkShadowShader;
    internal ChunkDrawBatch ChunkDraws => _chunkDraws;
    /// <summary>The opaque chunk pass's Hi-Z occlusion cull (<see cref="TerrainPasses.RenderWorld"/>).</summary>
    internal ChunkOcclusionCuller Occlusion { get; private set; }
    internal Shader ChunkShader => chunkShader;
    internal int LocChunkViewProj => _locChunkViewProj;
    internal int LocChunkDrawBase => _locChunkDrawBase;
    internal int LocChunkAlphaCutoff => _locChunkAlphaCutoff;
    internal float ClipY { get => _clipY; set => _clipY = value; }
    internal DamageVignetteRenderer? Vignette => _vignette;

    public RenderManager(IWindow window)
    {
        gl = window.CreateOpenGL();
        Units = new TextureUnits(gl);
        gl.Enable(GLEnum.DepthTest);
        gl.DepthFunc(GLEnum.Lequal);
        Targets = new RenderTargets(gl, this);
        Targets.ViewportSize = window.Size;
        Lighting = new FrameLighting(this);
        Sky = new SkyPasses(gl, this);
        Billboards = new BillboardPasses(this);
        Objects = new ObjectPasses(gl, this);
        Terrain = new TerrainPasses(gl, this);
        Reflection = new ReflectionPass(gl, this);
        UiPasses = new UiPasses(gl, this);

        // Which GPU actually ran matters for every timing: a hybrid laptop happily renders
        // this on the iGPU unless told otherwise, and the numbers then mean nothing.
        Log.WriteLine($"GPU: {gl.GetStringS(StringName.Renderer)} | {gl.GetStringS(StringName.Vendor)} | GL {gl.GetStringS(StringName.Version)}");

        GpuTimer = new GpuPassTimer(gl);

        LoadResources();
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

        // sRGB: the world samples linear albedo and the post pass tonemaps and re-encodes.
        textureAtlas = TextureAtlas.Build(gl, "assets/textures/blocks", srgb: true);
        // The average stands in for decoded atlas albedo on the far LOD, so decode it too.
        _lodLeafColor = ColorSpace.SrgbToLinear(
            LoadOpaqueAverage("assets/textures/blocks/oak_leaves.png", _lodLeafColor));
        ContentRegistry.BakeModels();
        // After the bake, the first point that knows which tiles cutout blocks wear: their
        // box-filtered mips would average leaf alpha under the cutoff and thin canopies out.
        TextureAtlas.ApplyCutoutMips(gl, textureAtlas, BlockType.CutoutTiles(), LightingConfig.AlphaCutoff);
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
        chunkShader.SetUniform("uStatusTint", Vector4.Zero);
        chunkShader.SetUniform("uAlphaCutoff", LightingConfig.AlphaCutoff);
        // The HDR target first, so every creator after it restores to the scene target it made.
        Targets.CreateHdrTarget();
        Reflection.CreateReflectionTarget();
        Targets.CreateSceneCopy();
        Targets.CreateRaysTarget();

        // Everything a world draw sets per draw, resolved once here (see SetDrawUniforms)
        _locModel = worldShader.GetUniformLocation("uModel");
        _locMvp = worldShader.GetUniformLocation("uMVP");
        _locAlpha = worldShader.GetUniformLocation("uAlpha");
        // Cache only: the white default is written in InitWorldProgram, where the program is
        // bound (SetUniform writes into whichever program is current, and the target creators
        // above have bound others by now).
        _locBiomeTint = worldShader.GetUniformLocation("uBiomeTint");
        _locEntitySun = worldShader.GetUniformLocation("uEntitySun");
        _locEntityBlock = worldShader.GetUniformLocation("uEntityBlock");
        _locEntityTint = worldShader.GetUniformLocation("uEntityTint");
        _locFlash = worldShader.GetUniformLocation("uFlash");
        _locHitFlash = worldShader.GetUniformLocation("uHitFlash");
        _locStatusTint = worldShader.GetUniformLocation("uStatusTint");
        _locAlphaCutoff = worldShader.GetUniformLocation("uAlphaCutoff");
        _locPointPos = worldShader.GetUniformArrayLocations("uPointPos", LightingConfig.MaxPointLights);
        _locPointColor = worldShader.GetUniformArrayLocations("uPointColor", LightingConfig.MaxPointLights);
        _locBlockLightPalette = worldShader.GetUniformArrayLocations("uBlockLightPalette", BlockLightPalette.MaxSlots);
        _blockLightPalette = BlockLightPalette.Linear.ToArray();

        _locChunkViewProj = chunkShader.GetUniformLocation("uViewProj");
        _locChunkDrawBase = chunkShader.GetUniformLocation("uDrawBase");
        _locChunkAlphaCutoff = chunkShader.GetUniformLocation("uAlphaCutoff");
        _locChunkPointPos = chunkShader.GetUniformArrayLocations("uPointPos", LightingConfig.MaxPointLights);
        _locChunkPointColor = chunkShader.GetUniformArrayLocations("uPointColor", LightingConfig.MaxPointLights);
        _locChunkBlockLightPalette = chunkShader.GetUniformArrayLocations("uBlockLightPalette", BlockLightPalette.MaxSlots);

        selectionShader = new Shader(gl, selectionVs, selectionFs);
        breakShader = new Shader(gl, breakVs, breakFs);
        // The crack sprite is a single column of BreakOverlayFrames stacked 32x32 frames, so
        // each frame becomes its own array slice and no mip level bleeds a neighbor in.
        _breakProgressTexture = TextureLoader.LoadSpritesheet(gl, "assets/textures/environment/break_progress.png",
            1, PlayerConfig.BreakOverlayFrames, PlayerConfig.BreakOverlayFrames, srgb: true);
        breakShader.Use();
        breakShader.SetUniform("uBreakProgress", 0);
        breakShader.SetUniform("uOpacity", PlayerConfig.BreakOverlayOpacity);

        shadowShader = new Shader(gl, shadowVs, shadowFs);
        // The depth pass still samples the atlas, to punch the cutout tiles' holes
        shadowShader.Use();
        shadowShader.SetUniform("uTextureAtlas", 0);

        chunkShadowShader = new Shader(gl, shadowVs, shadowFs, ChunkDrawDefines);
        chunkShadowShader.Use();
        chunkShadowShader.SetUniform("uTextureAtlas", 0);
        Shadows = new ShadowPass(gl, this);
        CreateShadowMap();

        // The shared chunk and LOD geometry, and the ring the chunk passes build their draw
        // commands in. All four issue GL calls, so they belong here on the render thread; World
        // (and through it ChunkManager and Chunk.UpdateMesh, and LodManager) is handed the three
        // pools by Program.
        OpaqueChunkGeometry = new ChunkGeometryPool(gl, ChunkGeometryPool.OpaqueInitialUnits, "opaque");
        TranslucentChunkGeometry = new ChunkGeometryPool(gl, ChunkGeometryPool.TranslucentInitialUnits, "translucent");
        LodGeometry = new ChunkGeometryPool(gl, ChunkGeometryPool.LodInitialUnits, "lod");
        _chunkDraws = new ChunkDrawBatch(gl);
        Occlusion = new ChunkOcclusionCuller(gl, this);

        sunShader = new Shader(gl, sunVs, sunFs);
        sunShader.Use();
        sunShader.SetUniform("uSunTexture", 0);

        _sunTexture = TextureLoader.LoadFromFile(gl, "assets/textures/environment/sun.png", srgb: true);
        // The loader hard-codes Nearest for the block tiles; the 64px disc is scaled up a lot
        // on screen, so it needs Linear or its edge turns into a staircase.
        gl.BindTexture(TextureTarget.Texture2DArray, _sunTexture);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        _sunQuad = Primitives.CreateSquare(gl, 1f);
        // The moon rides the sun's quad and vertex shader, with its own sprite (Linear, like the sun's)
        moonShader = new Shader(gl, sunVs, FileUtils.ReadFile("assets/shaders/moon-fragment.glsl"));
        moonShader.Use();
        moonShader.SetUniform("uMoonTexture", 0);
        _moonTexture = TextureLoader.LoadFromFile(gl, "assets/textures/environment/moon.png", srgb: true);
        gl.BindTexture(TextureTarget.Texture2DArray, _moonTexture);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        _clouds = new CloudRenderer(gl, CloudShadowUnit);
        _cloudShadow = new CloudShadowTexture(gl);
        // Bound once for the whole run: nothing else ever touches unit 8 (see CloudShadowUnit).
        gl.ActiveTexture(TextureUnit.Texture0 + CloudShadowUnit);
        gl.BindTexture(TextureTarget.Texture2D, _cloudShadow.Handle);
        gl.ActiveTexture(TextureUnit.Texture0);
        _skyDome = new SkyRenderer(gl, CloudShadowUnit);
        Screen = new ScreenPasses(gl, this);
        _vignette = new DamageVignetteRenderer(gl);
        _leaves = new LeafParticleRenderer(gl);
        _fire = new FireParticleRenderer(gl);
        _bubbles = new BubbleParticleRenderer(gl);
        _sliceTrails = new SliceTrailRenderer(gl, linearOutput: true);
        _healthbars = new EntityHealthbarRenderer(gl);

        blockCubes = new BlockCubeCache(gl);
        itemMeshes = new ItemMeshCache(gl, srgb: true);
        // Item textures are uploaded lazily, on first sight, from inside the entity and UI passes,
        // and TextureLoader.Upload binds on whatever unit is live. UIRenderer shares this instance,
        // so one hook covers both callers.
        itemMeshes.GlStateChanged = Units.InvalidateTextureUnits;
        Stations = new StationPasses(gl, this);
        Players = new PlayerPasses(gl, this);
        Mobs = new MobPasses(gl, this);
        IcebergPasses = new IcebergPasses(gl, this);
        Chests = new ChestPasses(gl, this);
        ui = new UIRenderer(gl, textureAtlas, blockCubes, itemMeshes, atlasIsSrgb: true);
        // The damage numbers are laid out from the HUD's font; without one there is nothing to draw
        if (ui.Font is { } font)
        {
            _damageNumbers = new DamageNumberRenderer(gl, font);
            _noticePips = new NoticePipRenderer(gl, font);
        }
        Selection = new SelectionPasses(gl, this);
        // Last, with every target and unit in place: the cloud draw's first-use cost lands here
        // rather than in the first frames (CloudRenderer.WarmUp restores what it touches).
        _clouds.WarmUp(Units, SceneReadUnit);
        // Everything above binds raw - the atlas build, the loader, the sun's filter tweak and
        // the three render targets - so nothing the cache thinks it knows survives load.
        Units.InvalidateTextureUnits();
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
        // A sampler2DShadow left at the default unit 0 collides with the sampler2DArray atlas
        // there and fails at draw time.
        program.SetUniform("uShadowMapFar", FarShadowUnit);
        program.SetUniform("uReflectionMap", 2);
        program.SetUniform("uSceneColor", 3);
        program.SetUniform("uSceneDepth", 4);
        program.SetUniform("uMaterialAtlas", MaterialUnit);
        program.SetUniform("uCloudShadowMap", CloudShadowUnit);
        program.SetUniform("uCloudHeight", SkyConfig.CloudHeight);
        program.SetUniform("uCloudField", SkyConfig.CloudFieldSize);
        // Doubles as the switch for the whole reflective path: left at 0 the shader never reads
        // the material buffer, which is what keeps the ItemDebugger (same shaders, no buffer)
        // and a world with no reflective tiles safe.
        program.SetUniform("uMaterialCount", materialSsbo != 0 ? TileMaterials.ByLayer.Length : 0);
        program.SetUniform("uOreSpecScale", SurfaceShadingConfig.OreSpecLinearScale);
        // Texels of the reflective tiles flashing now and then in the sun (OreTwinkle)
        program.SetUniform("uOreTwinkle", SurfaceShadingConfig.OreTwinkleStrength);
        program.SetUniform("uOreTwinkleDensity", SurfaceShadingConfig.OreTwinkleDensity);
        program.SetUniform("uOreTwinkleRate",
            new Vector2(SurfaceShadingConfig.OreTwinkleRateMin, SurfaceShadingConfig.OreTwinkleRateMax));
        program.SetUniform("uOreTwinkleWidth", SurfaceShadingConfig.OreTwinkleWidth);
        program.SetUniform("uOreTwinkleFade",
            new Vector2(SurfaceShadingConfig.OreTwinkleFadeNearBlocks, SurfaceShadingConfig.OreTwinkleFadeFarBlocks));
        // The snow glints key off the snow block's own tile; strength 0 (a program that never sets
        // it, like the ItemDebugger's) leaves the term off whatever slice that is.
        program.SetUniform("uSnowLayer", (float)ContentRegistry.RequireBlock("snow").Top);
        program.SetUniform("uSnowSparkle", SurfaceShadingConfig.SnowSparkleStrength);
        program.SetUniform("uSnowSparkleDensity", SurfaceShadingConfig.SnowSparkleDensity);
        // Leaves lit through from behind (LeafBacklight); strength 0 (the ItemDebugger) leaves it off
        program.SetUniform("uLeafBacklight", SurfaceShadingConfig.LeafBacklightStrength);
        program.SetUniform("uLeafBacklightPower", SurfaceShadingConfig.LeafBacklightPower);
        program.SetUniform("uLeafBacklightReach", SurfaceShadingConfig.LeafBacklightReachBlocks);
        program.SetUniform("uLeafBacklightTint", ColorSpace.SrgbToLinear(SurfaceShadingConfig.LeafBacklightTint));
        // Bands rolling over the grass tops with the wind (GrassShimmer); 0 strength leaves it off
        program.SetUniform("uGrassShimmer", SurfaceShadingConfig.GrassShimmerStrength);
        program.SetUniform("uGrassShimmerWavelength", SurfaceShadingConfig.GrassShimmerWavelengthBlocks);
        program.SetUniform("uGrassShimmerFadeNear", SurfaceShadingConfig.GrassShimmerFadeNearBlocks);
        program.SetUniform("uGrassShimmerFadeFar", SurfaceShadingConfig.GrassShimmerFadeFarBlocks);
        // The ice tile's sky sheen and sun highlight (IceGlint); 0 strength leaves both off
        program.SetUniform("uIceLayer", (float)ContentRegistry.RequireBlock("ice").Top);
        program.SetUniform("uIceGlint", SurfaceShadingConfig.IceGlintStrength);
        program.SetUniform("uIceShine", SurfaceShadingConfig.IceGlintShine);
        program.SetUniform("uIceSheen", SurfaceShadingConfig.IceSheenStrength);
        // Glass blocks and panes share the glass tile; their grazing sky tint (GlassSheen)
        program.SetUniform("uGlassLayer", (float)ContentRegistry.RequireBlock("glass").Top);
        program.SetUniform("uGlassSheen", SurfaceShadingConfig.GlassSheenStrength);
        // The battle tower dome's self-lit film (ForcefieldShimmer); 0 strength leaves its plain tile
        program.SetUniform("uForcefieldLayer", (float)ContentRegistry.RequireBlock("tower_forcefield").Top);
        program.SetUniform("uForcefield", SurfaceShadingConfig.ForcefieldStrength);
        program.SetUniform("uForcefieldColor",
            ColorSpace.SrgbToLinear(SurfaceShadingConfig.ForcefieldColor) * SurfaceShadingConfig.ForcefieldGlow);
        program.SetUniform("uForcefieldTerms", new Vector4(SurfaceShadingConfig.ForcefieldFilmAlpha,
            SurfaceShadingConfig.ForcefieldRimAlpha, SurfaceShadingConfig.ForcefieldHexAlpha, SurfaceShadingConfig.ForcefieldBandAlpha));
        program.SetUniform("uForcefieldCells", SurfaceShadingConfig.ForcefieldHexCellsPerBlock);
        program.SetUniform("uForcefieldFade",
            new Vector2(SurfaceShadingConfig.ForcefieldDetailFadeNearBlocks, SurfaceShadingConfig.ForcefieldDetailFadeFarBlocks));
        program.SetUniform("uReflValid", 0f);
        // The water model's tuning constants never change at runtime, so they go up once here
        // instead of riding ApplyFrameUniforms every frame.
        program.SetUniform("uWaterNormalScale", WaterRenderConfig.WaterNormalScale);
        program.SetUniform("uWaterRefractStrength", WaterRenderConfig.WaterRefractStrength);
        program.SetUniform("uWaterWindowRefract", WaterRenderConfig.WaterWindowRefractScale);
        program.SetUniform("uWaterAbsorb", WaterRenderConfig.WaterAbsorb);
        program.SetUniform("uWaterScatter", ColorSpace.SrgbToLinear(WaterRenderConfig.WaterScatter));
        program.SetUniform("uWaterCausticStrength", WaterRenderConfig.WaterCausticStrength);
        program.SetUniform("uWetDarken", SurfaceShadingConfig.WetDarken);
        program.SetUniform("uWaterFancyNear", WaterRenderConfig.WaterFancyNearBlocks);
        program.SetUniform("uWaterFancyFar", WaterRenderConfig.WaterFancyFarBlocks);
        program.SetUniform("uWaterFlowStretch", WaterRenderConfig.WaterFlowStretch);
        program.SetUniform("uWaterFlowScroll", WaterRenderConfig.WaterFlowScrollSpeed);
        program.SetUniform("uWaterFlowPeriod", WaterRenderConfig.WaterFlowPeriod);
        program.SetUniform("uWaterFallScroll", WaterRenderConfig.WaterFallScrollSpeed);
        program.SetUniform("uWaterFallStreak", WaterRenderConfig.WaterFallStreakStrength);
        program.SetUniform("uWaterFoamWidth", WaterRenderConfig.WaterFoamWidthBlocks);
        program.SetUniform("uWaterFoamStrength", WaterRenderConfig.WaterFoamStrength);
        program.SetUniform("uWaterFoamColor", ColorSpace.SrgbToLinear(WaterRenderConfig.WaterFoamColor));
        RippleUniforms.Init(program);
        // Hashed sun sparkles on the waves (WaterGlitter); 0 strength leaves them off
        program.SetUniform("uWaterGlitter", WaterRenderConfig.WaterGlitterStrength);
        program.SetUniform("uWaterGlitterCells", WaterRenderConfig.WaterGlitterCellsPerBlock);
        program.SetUniform("uWaterGlitterDensity", WaterRenderConfig.WaterGlitterDensity);
        program.SetUniform("uWaterGlitterTilt", WaterRenderConfig.WaterGlitterTiltRadians);
        program.SetUniform("uWaterGlitterRate", WaterRenderConfig.WaterGlitterRate);
        program.SetUniform("uWaterGlitterFade",
            new Vector2(WaterRenderConfig.WaterGlitterFadeNearBlocks, WaterRenderConfig.WaterGlitterFadeFarBlocks));
        program.SetUniform("uLavaLayer", (float)ContentRegistry.RequireBlock("lava").Top);
        var lavaSheet = TextureSheetRegistry.TryGet("lava");
        program.SetUniform("uLavaFrames", (float)(lavaSheet?.Frames ?? 1));
        program.SetUniform("uLavaFps", lavaSheet?.Fps ?? 0f);
        program.SetUniform("uLavaNormalScale", SurfaceShadingConfig.LavaNormalScale);
        program.SetUniform("uLavaCatchLight", SurfaceShadingConfig.LavaCatchLightStrength);
        program.SetUniform("uLavaCatchPower", SurfaceShadingConfig.LavaCatchLightPower);
        program.SetUniform("uLavaPulse", SurfaceShadingConfig.LavaPulseStrength);
        program.SetUniform("uLodLeafColor", _lodLeafColor);
        // The plain path's per-draw biome tint (mining pop, debris) must read as white for every
        // other draw, GLSL's zero default would draw a flagged vertex black; the chunk program
        // has no such uniform and -1 is a silent no-op.
        program.SetUniform("uBiomeTint", Vector3.One);
        program.SetUniform("uWaterPlaneY", WaterPlaneY);
        program.SetUniform("uWaterSsrSteps", WaterRenderConfig.WaterSsrSteps);
        program.SetUniform("uWaterSsrStepStart", WaterRenderConfig.WaterSsrStepStart);
        program.SetUniform("uWaterSsrStepGrowth", WaterRenderConfig.WaterSsrStepGrowth);
        program.SetUniform("uWaterSsrThickness", WaterRenderConfig.WaterSsrThickness);
        program.SetUniform("uWaterSsrPlaneNear", WaterRenderConfig.WaterSsrPlaneNear);
        program.SetUniform("uWaterSsrPlaneFar", WaterRenderConfig.WaterSsrPlaneFar);
        program.SetUniform("uWaterSsrMode", 0f);
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
    /// <see cref="ReflectionPass.RenderReflection"/> writes at three points in the frame. Water is drawn through
    /// the chunk program, so a value pushed to only one of the two shows up as a missing
    /// reflection. Leaves <c>worldShader</c> bound, like ApplyFrameUniforms.
    /// </summary>
    internal void SetWorldUniform(string name, float value)
    {
        chunkShader.Use();
        chunkShader.SetUniform(name, value);
        worldShader.Use();
        worldShader.SetUniform(name, value);
    }

    /// <inheritdoc cref="SetWorldUniform(string, float)"/>
    internal void SetWorldUniform(string name, in Matrix4x4 value)
    {
        chunkShader.Use();
        chunkShader.SetUniform(name, value);
        worldShader.Use();
        worldShader.SetUniform(name, value);
    }

    /// <summary>
    /// Allocates both shadow cascades' maps (<see cref="ShadowCascade.Create"/>), then restores
    /// the scene target and resets the texture-unit tracking the raw binds went around.
    /// </summary>
    private void CreateShadowMap()
    {
        _nearShadow.Create(gl);
        _farShadow.Create(gl);

        // Back to the scene target (the HDR FBO once it exists, framebuffer 0 before that).
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Targets.HdrFbo);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        // Raw binds inside Create, and the cascades' depth textures are brand-new names.
        Units.InvalidateTextureUnits();
    }

    public void SetViewport(Vector2D<int> size)
    {
        Targets.ViewportSize = size;
        gl.Viewport(size);
        // The HDR target first, so the creators after it restore to the resized scene target.
        Targets.CreateHdrTarget();
        Reflection.CreateReflectionTarget();
        Targets.CreateSceneCopy();
        Targets.CreateRaysTarget();
        // Belt-and-braces: each creator already invalidates on its own recreate path, but a
        // resize is rare enough that a second reset costs nothing and cannot be forgotten.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Binds and clears the HDR scene target (color to the flat sky fallback, and depth), paints the sky dome and the sun over
    /// it (<see cref="SkyPasses.RenderSky"/>), and leaves the GL state the world passes expect.
    /// <paramref name="camera"/> only orients the dome and the sun quad; a null camera skips
    /// both and leaves the flat clear color.
    /// </summary>
    public void BeginFrame(Camera? camera)
    {
        // The once-a-frame hard reset for the texture-unit cache: the UI pass, the particle
        // renderers and anything that rebuilt a GL texture ran since the last one.
        Units.InvalidateTextureUnits();
        Targets.BindSceneTarget();
        gl.ClearColor(Lighting.SkyColor.X, Lighting.SkyColor.Y, Lighting.SkyColor.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Straight onto the cleared target, before any terrain: see SkyPasses.RenderSky. The
        // rotation is the camera's own view with the translation dropped, so it keeps the sprint
        // and shake roll the world is drawn with and the dome's horizon stays level with the
        // terrain's.
        if (camera != null)
        {
            var rot = camera.GetViewMatrix();
            rot.M41 = rot.M42 = rot.M43 = 0f;
            Sky.RenderSky(rot, camera.GetProjectionMatrix(), Lighting.SkyFrame);
        }

        // Exactly once a frame, before any pass appends to it: the ring rolls onto its next
        // slot and waits on the fence that says the GPU is done reading it.
        _chunkDraws.BeginFrame();

        chunkShader.Use();
        // Opaque default: punch the leaf tiles' holes; the translucent pass lowers it.
        chunkShader.SetUniform(_locChunkAlphaCutoff, LightingConfig.AlphaCutoff);
        worldShader.Use();
        worldShader.SetUniform(_locAlphaCutoff, LightingConfig.AlphaCutoff);
        _eyeWaterDepth = camera is { IsEyeInWater: true, IsEyeInLava: false }
            ? camera.EyeWaterSurfaceY - camera.RenderEye.Y
            : 0f;
        ApplyFrameUniforms();
        gl.Enable(EnableCap.Blend);
        // The world passes rely on straight alpha blending for the fluid mesh and the chunk
        // fade-in. Set it here rather than inheriting whatever the last UI pass (or the sun's
        // additive blend above) left behind.
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    /// <summary>
    /// Binds the world textures for a world draw: the atlas on unit 0, the near shadow map on
    /// unit 1, the reflection texture on unit 2, the scene color/depth copies on units 3
    /// and 4, the material atlas on unit 6 and the far shadow map on unit 7. Unit 0 is left active on the way out, because <see cref="ObjectPasses.RenderItemDrops"/>
    /// binds an item's own texture assuming that; the trailing unconditional
    /// <see cref="TextureUnits.ActiveUnit"/> call is what upholds that postcondition now that the five binds
    /// themselves may skip their selects.
    /// <para>PERF_TODO R5: this runs ~18x a frame, most of them back-to-back inside the entity
    /// pass, and it used to cost 10 GL calls every time. A replay whose five bindings all still
    /// match costs 0; the dominant entity-pass case - unit 0 swapped away to an item sprite and
    /// back - costs 1.</para>
    /// </summary>
    internal void BindWorldTextures()
    {
        Units.BindLazy(1, TextureTarget.Texture2D, _nearShadow.DepthTex);
        Units.BindLazy(2, TextureTarget.Texture2D, Reflection.ReflTex);
        Units.BindLazy(3, TextureTarget.Texture2D, Targets.SceneColorTex);
        Units.BindLazy(4, TextureTarget.Texture2D, Targets.SceneDepthTex);
        Units.BindLazy(0, TextureTarget.Texture2DArray, textureAtlas);
        Units.BindLazy(MaterialUnit, TextureTarget.Texture2DArray, materialAtlas);
        Units.BindLazy(FarShadowUnit, TextureTarget.Texture2D, _farShadow.DepthTex);
        Units.ActiveUnit(0);
    }

    /// <summary>
    /// <see cref="BindWorldTextures"/> for a draw into the water mirror: identical, except that
    /// unit 2 is bound to 0 instead of the mirror's own texture (<see cref="ReflectionPass.ReflTex"/>).
    /// While the mirror's framebuffer is the draw target that texture must not be bound, or the
    /// draw is a rendering feedback loop (see the unit-2 unbind at the top of
    /// <see cref="ReflectionPass.RenderReflection"/>'s real-render path). Same postcondition:
    /// unit 0 is left active.
    /// </summary>
    private void BindWorldTexturesForMirror()
    {
        Units.BindLazy(1, TextureTarget.Texture2D, _nearShadow.DepthTex);
        Units.BindLazy(2, TextureTarget.Texture2D, 0);
        Units.BindLazy(3, TextureTarget.Texture2D, Targets.SceneColorTex);
        Units.BindLazy(4, TextureTarget.Texture2D, Targets.SceneDepthTex);
        Units.BindLazy(0, TextureTarget.Texture2DArray, textureAtlas);
        Units.BindLazy(MaterialUnit, TextureTarget.Texture2DArray, materialAtlas);
        Units.BindLazy(FarShadowUnit, TextureTarget.Texture2D, _farShadow.DepthTex);
        Units.ActiveUnit(0);
    }

    /// <summary>The world-texture bind an entity draw core uses: the mirror-safe one while it
    /// draws into the water mirror, the ordinary one otherwise.</summary>
    internal void BindEntityTextures(bool mirror)
    {
        if (mirror) BindWorldTexturesForMirror();
        else BindWorldTextures();
    }

    /// <summary>
    /// Pushes every world-shader uniform that is constant for the whole frame onto *both* world
    /// programs — the plain one and the CHUNK_DRAW variant the chunk passes draw through, which
    /// has its own copy of every uniform. Called once per frame from <see cref="BeginFrame"/> —
    /// uniform values live in the program object and survive any number of <c>UseProgram</c>
    /// switches, so the selection, break, sun and UI shaders running in between do not disturb
    /// these and every later world pass in the frame inherits them. Everything it reads is final
    /// by then: <see cref="FrameLighting.SetLighting"/> (including <c>_nearShadow.ViewProj</c>) runs before
    /// <see cref="BeginFrame"/>, and nothing in a world pass writes these fields.
    /// <c>uFlash</c>/<c>uHitFlash</c>/<c>uStatusTint</c> are entity-only (primed TNT / hurt or
    /// status-affected mobs), so they are reset on the plain program alone.
    /// Leaves <c>worldShader</c> bound, which is what every caller assumes.
    /// </summary>
    internal void ApplyFrameUniforms()
    {
        chunkShader.Use();
        ApplyFrameUniformsTo(chunkShader, _locChunkPointPos, _locChunkPointColor, _locChunkBlockLightPalette);
        worldShader.Use();
        ApplyFrameUniformsTo(worldShader, _locPointPos, _locPointColor, _locBlockLightPalette);
        worldShader.SetUniform(_locFlash, 0f);
        worldShader.SetUniform(_locHitFlash, 0f);
        worldShader.SetUniform(_locStatusTint, Vector4.Zero);
    }

    /// <summary>The body of <see cref="ApplyFrameUniforms"/>, run once per world program.
    /// <paramref name="program"/> must already be bound.</summary>
    private void ApplyFrameUniformsTo(Shader program, int[] locPointPos, int[] locPointColor, int[] locBlockLightPalette)
    {
        program.SetUniform("uTime", Lighting.Time);
        program.SetUniform("uSunDir", Lighting.SunDir);
        program.SetUniform("uSunColor", Lighting.SunColor);
        program.SetUniform("uSunIntensity", Lighting.SunIntensity);
        program.SetUniform("uAmbient", Lighting.Ambient);
        // Derived here, not in FrameLighting.SetLighting, which returns early on every dry frame
        program.SetUniform("uGroundAmbient", GroundAmbientOf(Lighting.Ambient));
        program.SetUniformArray3(locBlockLightPalette, _blockLightPalette);
        program.SetUniform("uTorchIntensity", TorchIntensity);
        program.SetUniform("uFogColor", Lighting.FogColor);
        program.SetUniform("uFogNear", Lighting.FogNear);
        program.SetUniform("uFogFar", Lighting.FogFar);
        // The dome's palette, so the fog samples the sky behind each fragment (SkySample).
        program.SetUniform("uSkyZenith", Lighting.Sky.Zenith);
        program.SetUniform("uSkyHorizon", Lighting.Sky.Horizon);
        program.SetUniform("uSkyGlow", Lighting.Sky.Glow);
        program.SetUniform("uSkyShape", SkyPalette.Shape);
        program.SetUniform("uFogWet", Lighting.FogWet);
        program.SetUniform("uCameraPos", Lighting.CameraPos);
        program.SetUniform("uNumPointLights", Lighting.PointCount);
        program.SetUniform("uFireLightStart", Lighting.FireLightStart);
        program.SetUniform("uFireLightRange", LightingConfig.FireLightRange);
        program.SetUniformArray3(locPointPos, Lighting.PointPos);
        program.SetUniformArray3(locPointColor, Lighting.PointColors);
        program.SetUniform("uSwayStrength", GameSettings.LeafSway);
        // uWind carries the leaf lean in its length (direction * WindLean), see test-vertex.glsl
        program.SetUniform("uWind", Lighting.WindDirection * SkyConfig.WindLean);
        program.SetUniform("uWindOffset", Lighting.WindOffset);
        // The water look rides here (not the one-time init) so the settings sliders act live.
        program.SetUniform("uWaterNormalScale", GameSettings.WaterWaves);
        program.SetUniform("uWaterRefractStrength", GameSettings.WaterRefraction);
        program.SetUniform("uWaterCausticStrength", GameSettings.WaterCaustics);
        RippleUniforms.Apply(program, WaterRipples);
        program.SetUniform("uLightViewProj", _nearShadow.ViewProj);
        program.SetUniform("uShadowStrength", Lighting.ShadowStrength);
        program.SetUniform("uShadowTexel", 1f / LightingConfig.ShadowMapSize);
        program.SetUniform("uShadowWorldTexel", _nearShadow.WorldTexel);
        program.SetUniform("uShadowSoftness", LightingConfig.ShadowSoftnessTexels);
        program.SetUniform("uShadowNormalOffset", LightingConfig.ShadowNormalOffsetTexels);
        program.SetUniform("uShadowBiasTexels", LightingConfig.ShadowBiasTexels);
        program.SetUniform("uShadowBiasScale", _nearShadow.BiasScale);
        program.SetUniform("uLightViewProjFar", _farShadow.ViewProj);
        program.SetUniform("uShadowWorldTexelFar", _farShadow.WorldTexel);
        program.SetUniform("uShadowBiasScaleFar", _farShadow.BiasScale);
        // Cloud shadows track the drawn clouds: the same clock (Lighting.TimeD, window time plus
        // Program.timeOffset) CloudRenderer.Render drifts the mesh by. Rides the frame (not the
        // one-time init) so turning the clouds off in the settings takes their shadows with them.
        program.SetUniform("uCloudShadowStrength",
            GameSettings.CloudOpacity > 0f ? SkyConfig.CloudShadowStrength : 0f);
        program.SetUniform("uCloudDrift", CloudCopies.Drift(Lighting.TimeD, SkyConfig.CloudFieldSize));
        program.SetUniform("uClipY", _clipY);
        // The water pass reads the scene copies by screen uv and unprojects their depth; both
        // describe the real eye, so the reflection pass (which draws no water) can ignore them.
        program.SetUniform("uInvViewport",
            new Vector2(1f / Math.Max(1, Targets.ViewportSize.X), 1f / Math.Max(1, Targets.ViewportSize.Y)));
        program.SetUniform("uInvViewProj", Lighting.InvViewProj);
        program.SetUniform("uSceneViewProj", Lighting.SceneViewProj);
        program.SetUniform("uShadowStrengthDry", Lighting.ShadowStrengthDry);
        program.SetUniform("uUnderwater", Lighting.EyeInLava ? 0f : MathUtils.SmoothStep(Lighting.UnderwaterT));
        // The wet fog darkens and loses red with the eye's depth (DeepWaterFog), eased in with the
        // underwater tint; nothing in lava, whose fog is its own red glow
        program.SetUniform("uDeepWaterDim",
            Lighting.EyeInLava ? Vector3.Zero : DeepWaterFog.Dim(_eyeWaterDepth) * MathUtils.SmoothStep(Lighting.UnderwaterT));
    }

    /// <summary>
    /// Everything a single world draw has to say: its model matrix (the vertex shader needs it
    /// for world positions and normals), its full MVP and its fade alpha. Exactly three
    /// <c>glUniform</c> calls through cached locations — the rest of the world shader's state
    /// was uploaded once by <see cref="ApplyFrameUniforms"/>.
    /// Entity draws only: chunks and LOD regions both go through <see cref="ChunkDrawBatch"/>,
    /// where a uniform could not vary between the sub-draws of one multi-draw anyway.
    /// <paramref name="light"/> is the world light the draw is lit by (the plain variant ignores
    /// the vertices' baked sun/block/tint). This is the only writer of <c>uEntitySun</c>/<c>uEntityBlock</c>/<c>uEntityTint</c>,
    /// so every plain-worldShader draw must go through it or it inherits the previous draw's light.
    /// </summary>
    internal void SetDrawUniforms(in Matrix4x4 model, in Matrix4x4 mvp, float alpha, in EntityLightSample light)
    {
        worldShader.SetUniform(_locModel, model);
        worldShader.SetUniform(_locMvp, mvp);
        worldShader.SetUniform(_locAlpha, alpha);
        worldShader.SetUniform(_locEntitySun, light.Sun);
        worldShader.SetUniform(_locEntityBlock, light.Block);
        worldShader.SetUniform(_locEntityTint, (int)light.Tint);
    }

    /// <summary>The world light at <paramref name="world"/>, read from <see cref="LightSource"/>.</summary>
    internal EntityLightSample LightAt(Vector3 world) => EntityLight.Sample(LightSource, world);

    public void Dispose()
    {
        GpuTimer.LogQueryCounts();

        ui.Dispose();
        blockCubes.Dispose();
        itemMeshes.Dispose();
        Stations.Dispose();
        Players.Dispose();
        IcebergPasses.Dispose();
        Chests.Dispose();
        Mobs.Dispose();
        worldShader.Dispose();
        chunkShader.Dispose();
        selectionShader.Dispose();
        breakShader.Dispose();
        shadowShader.Dispose();
        chunkShadowShader.Dispose();
        sunShader.Dispose();
        moonShader.Dispose();
        Occlusion.Dispose();
        gl.DeleteTexture(_moonTexture);
        _clouds?.Dispose();
        _cloudShadow?.Dispose();
        _skyDome?.Dispose();
        Screen.Dispose();
        _vignette?.Dispose();
        _leaves?.Dispose();
        _fire?.Dispose();
        _bubbles?.Dispose();
        _sliceTrails?.Dispose();
        _healthbars?.Dispose();
        _damageNumbers?.Dispose();
        _noticePips?.Dispose();
        _sunQuad.Dispose();
        gl.DeleteTexture(_sunTexture);
        gl.DeleteTexture(_breakProgressTexture);
        _nearShadow.Dispose(gl);
        _farShadow.Dispose(gl);
        Reflection.DeleteReflectionTarget();
        Targets.DeleteSceneCopy();
        Targets.DeleteRaysTarget();
        Targets.DeleteHdrTarget();
        Selection.Dispose();
        GpuTimer.Dispose();
        // The chunks and LOD regions that hold slices in these pools are gone by now (the world
        // is torn down through World.SaveAndDispose before the render manager is disposed), so
        // freeing the buffers here cannot orphan a live slice.
        _chunkDraws.Dispose();
        OpaqueChunkGeometry.Dispose();
        TranslucentChunkGeometry.Dispose();
        LodGeometry.Dispose();
        gl.Dispose();
    }
}
