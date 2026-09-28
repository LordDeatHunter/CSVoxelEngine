using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// A minimal standalone renderer for the item debugger: content registry, block atlas, the
/// world shader and the player/item meshes, without the game's chunk and LOD geometry pools
/// (which would allocate hundreds of megabytes to draw one model). Player and held-item
/// matrices come straight from <see cref="PlayerModel"/> and <see cref="RenderManager.PoseHeldItem"/>,
/// so the pose matches the game; lighting is fixed daylight so the transform is easy to read.
/// </summary>
public sealed class DebugSceneRenderer : IDisposable
{
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));
    private static readonly Vector3 SunColor = new(1f, 0.96f, 0.88f);
    private static readonly Vector3 Ambient = new(0.42f, 0.45f, 0.52f);
    private static readonly Vector3 FogColor = new(0.3f, 0.6f, 0.9f);
    private static readonly Vector3 TorchColor = new(1f, 0.62f, 0.25f);
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

    public PlayerModel.PlayerRig Rig { get; }
    public UIRenderer Ui { get; }

    public DebugSceneRenderer(GL gl)
    {
        _gl = gl;

        ContentRegistry.Load("assets");
        _atlasTexture = TextureAtlas.Build(gl, "assets/textures/blocks");
        ContentRegistry.BakeModels();

        _world = new Shader(gl,
            FileUtils.ReadFile("assets/shaders/test-vertex.glsl"),
            FileUtils.ReadFile("assets/shaders/test-fragment.glsl"));
        _world.Use();
        _world.SetUniform("uTextureAtlas", 0);
        _world.SetUniform("uShadowMap", 1);
        _world.SetUniform("uReflectionMap", 2);
        _world.SetUniform("uSceneColor", 3);
        _world.SetUniform("uSceneDepth", 4);
        // The plain vertex path lights meshes from these per-draw uniforms (the game samples the
        // world through RenderManager.SetDrawUniforms). The debugger has no world, so it lights
        // everything like the old vertex defaults: full sun, no block light. Unset they would be 0,
        // i.e. night ambient.
        _world.SetUniform("uEntitySun", 1f);
        _world.SetUniform("uEntityBlock", 0f);

        _locModel = _world.GetUniformLocation("uModel");
        _locMvp = _world.GetUniformLocation("uMVP");
        _locAlpha = _world.GetUniformLocation("uAlpha");
        _locAlphaCutoff = _world.GetUniformLocation("uAlphaCutoff");
        _locPointPos = _world.GetUniformArrayLocations("uPointPos", 8);
        _locPointColor = _world.GetUniformArrayLocations("uPointColor", 8);
        CreateDummyTextures();

        try
        {
            Rig = PlayerModel.PlayerRig.FromBbmodel(BbmodelLoader.Load(RenderManager.PlayerModelPath));
        }
        catch (Exception e)
        {
            Log.WriteLine($"Player model '{RenderManager.PlayerModelPath}' not used ({e.Message}); using built-in Steve.");
            Rig = PlayerModel.PlayerRig.BuiltIn();
        }

        _paletteTex = TextureLoader.Upload(gl, Rig.TexturePixels, Rig.TextureWidth, Rig.TextureHeight);
        _playerMeshes = PlayerModel.BuildMeshes(gl, Rig);
        _blockCubes = new BlockCubeCache(gl);
        _itemMeshes = new ItemMeshCache(gl);
        Ui = new UIRenderer(gl, _atlasTexture, _blockCubes, _itemMeshes);
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
            var model = RenderManager.PoseHeldItem(hand, editor.DisplayMatrix(),
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
        _world.SetUniform("uTorchColor", TorchColor);
        _world.SetUniform("uTorchIntensity", 1.2f);
        _world.SetUniform("uFogColor", FogColor);
        _world.SetUniform("uFogNear", 200f);
        _world.SetUniform("uFogFar", 1800f);
        _world.SetUniform("uCameraPos", eyePos);
        _world.SetUniform("uNumPointLights", 0);
        _world.SetUniformArray3(_locPointPos, NoPoints);
        _world.SetUniformArray3(_locPointColor, NoPoints);
        _world.SetUniform("uSwayStrength", 0f);
        _world.SetUniform("uLightViewProj", Matrix4x4.Identity);
        _world.SetUniform("uShadowStrength", 0f);
        _world.SetUniform("uShadowStrengthDry", 0f);
        _world.SetUniform("uShadowTexel", 1f / WorldGenerationConfig.ShadowMapSize);
        _world.SetUniform("uShadowWorldTexel",
            2f * WorldGenerationConfig.ShadowRadiusBlocks / WorldGenerationConfig.ShadowMapSize);
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
        _world.Dispose();
        _gl.DeleteTexture(_paletteTex);
        _gl.DeleteTexture(_dummyColor);
        _gl.DeleteTexture(_dummyShadow);
        GC.SuppressFinalize(this);
    }
}
