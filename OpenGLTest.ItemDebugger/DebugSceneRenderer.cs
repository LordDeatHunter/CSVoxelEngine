using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Models;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Hud;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.ItemDebugger;

/// <summary>
/// A minimal standalone renderer for the item debugger: content registry, block atlas, the
/// world shader and the player/item meshes, without the game's chunk and LOD geometry pools
/// (which would allocate hundreds of megabytes to draw one model). Player and held-item
/// matrices come straight from <see cref="PlayerModel"/> and <see cref="RenderManager.PoseHeldItem"/>,
/// so the pose matches the game; lighting is fixed daylight so the transform is easy to read.
/// The ENTITIES tab draws an <see cref="EntityPreviewState"/> part by part
/// (<see cref="RenderEntity"/>), and the TREES tab draws a <see cref="TreePreviewState"/>'s
/// ground and grown tree, meshed by the real chunk mesher through <see cref="CellBatchMesher"/>
/// (<see cref="RenderTree"/>).
/// </summary>
public sealed class DebugSceneRenderer : IDisposable
{
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));
    private static readonly Vector3 SunColor = new(1f, 0.96f, 0.88f);
    private static readonly Vector3 Ambient = new(0.42f, 0.45f, 0.52f);
    private static readonly Vector3 FogColor = new(0.3f, 0.6f, 0.9f);
    private static readonly Vector3[] NoPoints = new Vector3[8];

    private readonly GL _gl;
    private readonly Shader _world;
    private readonly uint _atlasTexture;
    private readonly uint _paletteTex;
    private uint _dummyColor;
    private uint _dummyShadow;
    private readonly float _lavaLayer;
    private readonly Dictionary<string, Mesh> _playerMeshes;
    private readonly BlockCubeCache _blockCubes;
    private readonly ItemMeshCache _itemMeshes;
    private readonly int _locModel;
    private readonly int _locMvp;
    private readonly int _locAlpha;
    private readonly int _locAlphaCutoff;
    private readonly int[] _locPointPos;
    private readonly int[] _locPointColor;
    // Resolved in the ctor body after ContentRegistry.Load: a field initializer would run first,
    // against an empty registry.
    private readonly byte _flintId;
    // Entity rigs, built the first time the ENTITIES tab shows each one.
    private readonly Dictionary<string, EntityPreview> _entityPreviews = new();

    // One entity's per-part meshes (ordinal-sorted names, index-aligned), rig and skin.
    private sealed record EntityPreview(string[] PartNames, Mesh[] Parts, PlayerRig Rig, uint Texture);

    // The TREES tab's meshes (one per touched scratch chunk, in world cell coordinates) and the
    // TreePreviewState.Version they were built for; -1 forces the first build.
    private readonly List<Mesh> _treeMeshes = new();
    private int _treeVersion = -1;

    public PlayerRig Rig { get; }
    public UIRenderer Ui { get; }

    public DebugSceneRenderer(GL gl)
    {
        _gl = gl;

        ContentRegistry.Load("assets");
        _atlasTexture = TextureAtlas.Build(gl, "assets/textures/blocks", srgb: false);
        ContentRegistry.BakeModels();
        _flintId = ContentRegistry.RequireItem("flint").Id;

        _world = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/test-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/test-fragment.glsl"));
        _world.Use();
        _world.SetUniform("uTextureAtlas", 0);
        _world.SetUniform("uShadowMap", 1);
        // The far cascade shares the dummy shadow texture on unit 1: same sampler type, so it
        // never lands on unit 0 beside the sampler2DArray atlas.
        _world.SetUniform("uShadowMapFar", 1);
        _world.SetUniform("uReflectionMap", 2);
        _world.SetUniform("uSceneColor", 3);
        _world.SetUniform("uSceneDepth", 4);
        // An unset sampler2D defaults to unit 0, where the sampler2DArray atlas lives, and that
        // type clash fails the draw; unit 2 already holds a sampler2D. uCloudShadowStrength stays
        // at its default 0, which keeps the cloud-shadow path off here.
        _world.SetUniform("uCloudShadowMap", 2);
        _world.SetUniform("uOreSpecScale", WorldGenerationConfig.OreSpecLinearScale);
        _world.SetUniform("uWetDarken", WorldGenerationConfig.WetDarken);
        // The plain vertex path lights meshes from these per-draw uniforms (the game samples the
        // world through RenderManager.SetDrawUniforms). The debugger has no world, so it lights
        // everything like the old vertex defaults: full sun, no block light. Unset they would be 0,
        // i.e. night ambient.
        _world.SetUniform("uEntitySun", 1f);
        _world.SetUniform("uEntityBlock", 0f);
        _world.SetUniform("uEntityTint", 0);
        // The plain vertex path multiplies biome-tinted vertices (grass, leaves) by this per-draw
        // tint; unset it is black. The game writes the same white default in
        // RenderManager.InitWorldProgram.
        _world.SetUniform("uBiomeTint", Vector3.One);

        _locModel = _world.GetUniformLocation("uModel");
        _locMvp = _world.GetUniformLocation("uMVP");
        _locAlpha = _world.GetUniformLocation("uAlpha");
        _locAlphaCutoff = _world.GetUniformLocation("uAlphaCutoff");
        _locPointPos = _world.GetUniformArrayLocations("uPointPos", 8);
        _locPointColor = _world.GetUniformArrayLocations("uPointColor", 8);
        CreateDummyTextures();

        try
        {
            Rig = PlayerRig.FromBbmodel(BbmodelLoader.Load(RenderManager.PlayerModelPath));
        }
        catch (Exception e)
        {
            Log.WriteLine($"Player model '{RenderManager.PlayerModelPath}' not used ({e.Message}); using built-in Steve.");
            Rig = PlayerRig.BuiltIn();
        }

        _paletteTex = TextureLoader.Upload(gl, Rig.TexturePixels, Rig.TextureWidth, Rig.TextureHeight);
        _playerMeshes = PlayerRig.BuildMeshes(gl, Rig);
        _blockCubes = new BlockCubeCache(gl);
        _itemMeshes = new ItemMeshCache(gl);
        Ui = new UIRenderer(gl, _atlasTexture, _blockCubes, _itemMeshes, atlasIsSrgb: false);
        _lavaLayer = ContentRegistry.RequireBlock("lava").Top;
    }

    public void Render(Matrix4x4 viewProj, Vector3 eyePos, float bodyYaw, float bodyPitch,
        PlayerModel player, ItemDisplayEditor editor, float time, Vector2D<int> size, bool firstPerson)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _gl.ClearColor(0.53f, 0.64f, 0.76f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        // The game's entity pass never culls; match it so no player face can vanish.
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _world.Use();
        ApplyFrameUniforms(viewProj, eyePos, time, size);

        // First person is the game's framing: the head is not drawn and the body slides back so
        // the eye sits forward of the chest, which is also where the held item is anchored.
        var backOffset = firstPerson ? PlayerModel.FirstPersonBodyBack : 0f;
        BindWorldTextures();
        BindArray(0, _paletteTex);
        foreach (var part in PlayerModel.BodyParts)
            DrawPart(part, viewProj, eyePos, bodyYaw, bodyPitch, player, backOffset);
        if (!firstPerson)
            DrawPart("Head", viewProj, eyePos, bodyYaw, bodyPitch, player, backOffset);

        DrawHeldItem(viewProj, eyePos, bodyYaw, bodyPitch, player, editor, time, backOffset);
    }

    /// <summary>
    /// Draws the entity <paramref name="state"/> previews, part by part, at the origin: each part
    /// from the state's <see cref="EntityAnimator.GetPartMatrix"/>, the flint in the hand when the
    /// rig has one, and the hurt mode's hit-flash tint. Same frame setup as <see cref="Render"/>.
    /// </summary>
    public void RenderEntity(Matrix4x4 viewProj, Vector3 eyePos, EntityPreviewState state, float time,
        Vector2D<int> size)
    {
        BeginScene(viewProj, eyePos, time, size);
        if (state.Selected is not { } type) return;

        var preview = Preview(type);
        var animator = state.Animator;
        // The previewed mob's own pose on top of the clips (a goblin's lean, flinch, death fall and spawn pop,
        // a slime's squash) and where its feet are (a hopping slime travels).
        var feet = state.DrawOffset;
        var effect = state.BodyEffect();
        _world.SetUniform("uHitFlash", state.HitFlash);
        BindWorldTextures();
        BindArray(0, preview.Texture);
        for (var i = 0; i < preview.Parts.Length; i++)
            Draw(preview.Parts[i],
                animator.GetPartMatrix(preview.PartNames[i], preview.Rig, feet, state.FacingYaw) * effect, viewProj);

        if (state.ShowsHeldStone && _itemMeshes.TryGet(_flintId, out var stone, out var stoneTex))
        {
            BindArray(0, stoneTex);
            var model = Matrix4x4.CreateScale(Goblin.HeldStoneScale)
                        * animator.HandAnchor(Goblin.HandPart, preview.Rig, feet, state.FacingYaw) * effect;
            Draw(stone, model, viewProj);
        }
        _world.SetUniform("uHitFlash", 0f);
    }

    /// <summary>
    /// Draws the TREES tab: <paramref name="state"/>'s ground plane and grown tree
    /// (<see cref="TreePreviewState.MeshCells"/>). The meshes are rebuilt through
    /// <see cref="CellBatchMesher"/> only when <see cref="TreePreviewState.Version"/> moved, so a
    /// slider drag costs at most one rebuild per rendered frame. Same frame setup as
    /// <see cref="RenderEntity"/>.
    /// </summary>
    public void RenderTree(Matrix4x4 viewProj, Vector3 eyePos, TreePreviewState state, float time,
        Vector2D<int> size)
    {
        BeginScene(viewProj, eyePos, time, size);
        if (state.Selected is null) return;

        if (state.Version != _treeVersion)
        {
            RebuildTreeMeshes(state.MeshCells);
            _treeVersion = state.Version;
        }

        BindWorldTextures();
        foreach (var mesh in _treeMeshes)
            Draw(mesh, Matrix4x4.Identity, viewProj);
    }

    private void RebuildTreeMeshes(IReadOnlyList<TreeCell> cells)
    {
        foreach (var mesh in _treeMeshes) mesh.Dispose();
        _treeMeshes.Clear();

        var translucent = 0;
        foreach (var data in CellBatchMesher.Build(cells))
        {
            translucent += data.TranslucentVertices.Count;
            if (data.Vertices.Count == 0) continue;
            ToDisplaySpaceTint(data.Vertices);
            _treeMeshes.Add(new Mesh(_gl, CollectionsMarshal.AsSpan(data.Vertices),
                CollectionsMarshal.AsSpan(data.Indices)));
        }
        // Trees and grass mesh no translucent geometry; if a species ever does, say so rather than
        // silently leaving it out of the preview.
        if (translucent > 0)
            Log.Warn($"Tree preview: {translucent} translucent vertices are not drawn.");
    }

    // The chunk mesher bakes a biome tint as a *linear* multiplier against the decoded gray of the
    // textureless tiles (BiomeTint.VertexTint). The debugger samples its atlas undecoded and never
    // encodes, so that multiplier would lift a tinted face about three times too bright here. Rebake
    // it so the gray texel times Gain lands on the color the game displays: encode(grayLin * v * Gain).
    private static void ToDisplaySpaceTint(List<Vertex> vertices)
    {
        var grayLinear = ColorSpace.SrgbToLinear(WorldGenerationConfig.BiomeTintTextureGray) * BiomeTint.Gain;
        var grayDisplay = WorldGenerationConfig.BiomeTintTextureGray * BiomeTint.Gain;
        var span = CollectionsMarshal.AsSpan(vertices);
        for (var i = 0; i < span.Length; i++)
        {
            ref var v = ref span[i];
            if (!v.IsBiomeTinted) continue;
            var c = v.PackedColor;
            var r = Rebake(c & 0xFF);
            var g = Rebake((c >> 8) & 0xFF);
            var b = Rebake((c >> 16) & 0xFF);
            v.PackedColor = (c & 0xFF000000u) | r | (g << 8) | (b << 16);
        }

        uint Rebake(uint channel)
        {
            var display = ColorSpace.LinearToSrgb(channel / 255f * grayLinear) / grayDisplay;
            return (uint)Math.Clamp(MathF.Round(display * 255f), 0f, 255f);
        }
    }

    // The frame setup shared by the ENTITIES and TREES tabs.
    private void BeginScene(Matrix4x4 viewProj, Vector3 eyePos, float time, Vector2D<int> size)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _gl.ClearColor(0.53f, 0.64f, 0.76f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _world.Use();
        ApplyFrameUniforms(viewProj, eyePos, time, size);
    }

    private EntityPreview Preview(EntityType type)
    {
        if (_entityPreviews.TryGetValue(type.Name, out var cached)) return cached;

        var rig = PlayerRig.FromBbmodel(type.Model);
        var byName = PlayerRig.BuildMeshes(_gl, rig);
        var names = new string[byName.Count];
        byName.Keys.CopyTo(names, 0);
        Array.Sort(names, StringComparer.Ordinal);
        var meshes = new Mesh[names.Length];
        for (var i = 0; i < names.Length; i++) meshes[i] = byName[names[i]];
        // Plain (not sRGB) upload: this renderer is not linear-lit, like its atlas.
        var texture = TextureLoader.Upload(_gl, rig.TexturePixels, rig.TextureWidth, rig.TextureHeight);

        var preview = new EntityPreview(names, meshes, rig, texture);
        _entityPreviews[type.Name] = preview;
        return preview;
    }

    private void DrawHeldItem(Matrix4x4 viewProj, Vector3 eyePos, float bodyYaw, float bodyPitch,
        PlayerModel player, ItemDisplayEditor editor, float time, float backOffset)
    {
        if (!editor.HasSelection) return;

        var hand = player.GetHandAnchorMatrix(eyePos, bodyYaw, backOffset, bodyPitch);

        if (editor.IsPureItem && ItemRegistry.TryGet(editor.SelectedId) is { } type)
        {
            var frame = type.FrameAt(time, Compass.HeadingFromYaw(bodyYaw));
            if (!_itemMeshes.TryGetFrame(editor.SelectedId, frame, out var mesh, out var texture)) return;

            BindArray(0, texture);
            var model = RenderManager.PoseHeldItem(hand, editor.DisplayMatrix(player.HeldItemRotationOffset),
                editor.ItemHeldScale, editor.ItemGrip);
            Draw(mesh, model, viewProj);
            return;
        }

        if (_blockCubes.Get(editor.SelectedId) is not { } cube) return;
        BindWorldTextures();
        var blockModel = RenderManager.PoseHeldItem(hand, Matrix4x4.Identity,
            editor.BlockHeldScale, editor.BlockGrip);
        Draw(cube, blockModel, viewProj);
    }

    private void DrawPart(string part, in Matrix4x4 viewProj, Vector3 eyePos, float yaw, float pitch,
        PlayerModel player, float backOffset)
    {
        var model = player.GetPartMatrix(part, eyePos, yaw, pitch, backOffset);
        Draw(_playerMeshes[part], model, viewProj);
    }

    private void Draw(Mesh mesh, in Matrix4x4 model, in Matrix4x4 viewProj)
    {
        _world.SetUniform(_locModel, model);
        _world.SetUniform(_locMvp, model * viewProj);
        _world.SetUniform(_locAlpha, 1f);
        mesh.Render();
    }

    private void ApplyFrameUniforms(Matrix4x4 viewProj, Vector3 eyePos, float time, Vector2D<int> size)
    {
        _world.SetUniform("uTime", time);
        _world.SetUniform("uSunDir", SunDirection);
        _world.SetUniform("uSunColor", SunColor);
        _world.SetUniform("uSunIntensity", 1.2f);
        _world.SetUniform("uAmbient", Ambient);
        _world.SetUniform("uGroundAmbient", RenderManager.GroundAmbientOf(Ambient));
        _world.SetUniformArray3(_world.GetUniformArrayLocations("uBlockLightPalette", BlockLightPalette.MaxSlots),
            BlockLightPalette.Linear);
        _world.SetUniform("uTorchIntensity", 1.2f);
        _world.SetUniform("uFogColor", FogColor);
        _world.SetUniform("uFogNear", 200f);
        _world.SetUniform("uFogFar", 1800f);
        _world.SetUniform("uCameraPos", eyePos);
        _world.SetUniform("uNumPointLights", 0);
        _world.SetUniformArray3(_locPointPos, NoPoints);
        _world.SetUniformArray3(_locPointColor, NoPoints);
        _world.SetUniform("uSwayStrength", 0f);
        _world.SetUniform("uWind", new Vector2(1f, 0f));
        _world.SetUniform("uWindOffset", Vector2.Zero);
        _world.SetUniform("uLightViewProj", Matrix4x4.Identity);
        _world.SetUniform("uShadowStrength", 0f);
        _world.SetUniform("uShadowStrengthDry", 0f);
        _world.SetUniform("uShadowTexel", 1f / WorldGenerationConfig.ShadowMapSize);
        _world.SetUniform("uShadowWorldTexel",
            2f * WorldGenerationConfig.ShadowRadiusBlocks / WorldGenerationConfig.ShadowMapSize);
        // Harmless placeholders: uShadowStrength is 0 here, so SampleShadow never reads them.
        _world.SetUniform("uLightViewProjFar", Matrix4x4.Identity);
        _world.SetUniform("uShadowWorldTexelFar",
            2f * WorldGenerationConfig.FarShadowRadiusBlocks / WorldGenerationConfig.FarShadowMapSize);
        _world.SetUniform("uShadowBiasScaleFar", 1f / (8f * WorldGenerationConfig.FarShadowRadiusBlocks));
        _world.SetUniform("uShadowSoftness", WorldGenerationConfig.ShadowSoftnessTexels);
        _world.SetUniform("uShadowNormalOffset", WorldGenerationConfig.ShadowNormalOffsetTexels);
        _world.SetUniform("uClipY", -1e9f);
        _world.SetUniform("uInvViewport", new Vector2(1f / Math.Max(1, size.X), 1f / Math.Max(1, size.Y)));
        _world.SetUniform("uInvViewProj", Matrix4x4.Invert(viewProj, out var inv) ? inv : Matrix4x4.Identity);
        _world.SetUniform("uUnderwater", 0f);
        _world.SetUniform("uReflValid", 0f);
        _world.SetUniform("uReflViewProj", Matrix4x4.Identity);
        _world.SetUniform("uFlash", 0f);
        _world.SetUniform("uHitFlash", 0f);
        _world.SetUniform(_locAlphaCutoff, 0.5f);
        _world.SetUniform("uLodLeafColor", new Vector3(0.17f, 0.29f, 0.07f));
        _world.SetUniform("uLavaLayer", _lavaLayer);
    }

    private void BindWorldTextures()
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + 1);
        _gl.BindTexture(TextureTarget.Texture2D, _dummyShadow);
        for (var unit = 2; unit <= 4; unit++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, _dummyColor);
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);
    }

    private void BindArray(int unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2DArray, texture);
    }

    private unsafe void CreateDummyTextures()
    {
        _dummyColor = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _dummyColor);
        fixed (byte* white = new byte[] { 255, 255, 255, 255 })
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, white);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        // uShadowStrength is always 0 here, but the sampler has to be complete for the draw.
        _dummyShadow = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _dummyShadow);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24, 1, 1, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
        Span<float> border = [1f, 1f, 1f, 1f];
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)GLEnum.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        Ui.Dispose();
        _itemMeshes.Dispose();
        _blockCubes.Dispose();
        foreach (var mesh in _playerMeshes.Values) mesh.Dispose();
        foreach (var preview in _entityPreviews.Values)
        {
            foreach (var mesh in preview.Parts) mesh.Dispose();
            _gl.DeleteTexture(preview.Texture);
        }
        _entityPreviews.Clear();
        foreach (var mesh in _treeMeshes) mesh.Dispose();
        _treeMeshes.Clear();
        _world.Dispose();
        _gl.DeleteTexture(_paletteTex);
        _gl.DeleteTexture(_dummyColor);
        _gl.DeleteTexture(_dummyShadow);
        GC.SuppressFinalize(this);
    }
}
