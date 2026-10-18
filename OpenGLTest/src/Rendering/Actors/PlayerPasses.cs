using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content.Models;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Networking.Client;
using VoxelEngine.Player;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The player draws: the local player's body (first person, shoulder-surf and the glide
/// between them, or the whole body for the water mirror), the item in its right hand and every
/// remote player. Owns the player rig, built from its Blockbench project at load, with its
/// palette texture and one mesh per body part; the world program and the item and block caches
/// stay owned by <see cref="RenderManager"/>. Render thread only.
/// </summary>
internal sealed class PlayerPasses : IDisposable
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    private uint _playerPaletteTex;
    private Dictionary<string, Mesh> _playerMeshes = new();

    /// <summary>Blockbench project behind the player model; a missing or broken file falls back to built-in Steve.</summary>
    internal const string PlayerModelPath = "assets/models/player/player.bbmodel";

    /// <summary>The player shape being drawn: the Blockbench file when it loads, built-in Steve otherwise.</summary>
    internal PlayerRig PlayerRig { get; private set; } = PlayerRig.BuiltIn();

    // Live views of the palette and the part meshes, for the shadow pass.
    internal uint PlayerPaletteTex => _playerPaletteTex;
    internal Dictionary<string, Mesh> PlayerMeshes => _playerMeshes;

    private TextureUnits Units => _owner.Units;

    internal PlayerPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        try
        {
            PlayerRig = PlayerRig.FromBbmodel(BbmodelLoader.Load(PlayerModelPath));
        }
        catch (Exception e)
        {
            Log.WriteLine($"Player model '{PlayerModelPath}' not used ({e.Message}); using built-in Steve.");
        }
        _playerPaletteTex = TextureLoader.Upload(gl, PlayerRig.TexturePixels,
            PlayerRig.TextureWidth, PlayerRig.TextureHeight, srgb: true);
        _playerMeshes = PlayerRig.BuildMeshes(gl, PlayerRig);
    }

    /// <summary>
    /// Draws every remote player as the player body posed at their broadcast eye, using the client's
    /// own player rig and textures so all players look alike. Restores the world textures on the way
    /// out, like <see cref="MobPasses.RenderGoblins"/>.
    /// </summary>
    internal void RenderRemotePlayers(Camera camera, IReadOnlyList<RemotePlayer> players)
    {
        if (camera == null || _playerMeshes.Count == 0 || players.Count == 0)
            return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(false);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _playerPaletteTex);
        var viewProj = camera.GetViewProjection();
        foreach (var remote in players)
        {
            var light = _owner.LightAt(remote.Eye);
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = remote.Model.GetPartMatrix(part, remote.Eye, remote.Yaw, remote.Pitch);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                _playerMeshes[part].Render();
            }

            var head = remote.Model.GetPartMatrix("Head", remote.Eye, remote.Yaw, remote.Pitch);
            _owner.SetDrawUniforms(head, head * viewProj, 1f, light);
            _playerMeshes["Head"].Render();
        }

        _owner.BindEntityTextures(false);
    }

    /// <summary>
    /// Draws the player body at the camera's feet, facing with the camera yaw. First person
    /// draws everything but the head, shifted slightly back so the eye sits forward of the
    /// chest; shoulder-surf draws the full body (head included) unshifted at the true feet.
    /// The F5 glide uses one opaque shell sliding between those offsets while the head fades
    /// in on top, so no two shells ever coexist to z-fight. The shadow pass always draws the
    /// same body unshifted, head included.
    /// </summary>
    internal void RenderPlayer(Camera camera, PlayerModel player)
    {
        if (camera == null || player == null) return;
        DrawPlayer(camera, player, camera.GetViewProjection(), fullBody: false, mirror: false);
    }

    /// <summary>
    /// The body of <see cref="RenderPlayer"/> through an explicit <paramref name="viewProj"/>.
    /// <paramref name="fullBody"/> forces the shoulder-surf path (every part plus the head, opaque,
    /// unshifted) whatever the shoulder blend: the first-person head skip and back shift only
    /// protect the real eye, so a view from anywhere else (the water mirror) wants the whole body.
    /// </summary>
    internal void DrawPlayer(Camera camera, PlayerModel player, in Matrix4x4 viewProj, bool fullBody, bool mirror)
    {
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);

        Units.BindUnit(0, TextureTarget.Texture2DArray, _playerPaletteTex);

        var blend = fullBody ? 1f : camera.ShoulderBlend;
        var bodyYaw = camera.PlayerYaw;
        // One sample for the whole body: every part is lit alike, so no seam opens between them
        var light = _owner.LightAt(camera.Position);
        if (blend <= 0f)
        {
            foreach (var part in PlayerModel.FirstPersonParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch,
                    PlayerModel.FirstPersonBodyBack);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                _playerMeshes[part].Render();
            }
        }
        else if (blend >= 1f)
        {
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                _playerMeshes[part].Render();
            }
            var head = player.GetPartMatrix("Head", camera.Position, bodyYaw, camera.Pitch);
            _owner.SetDrawUniforms(head, head * viewProj, 1f, light);
            _playerMeshes["Head"].Render();
        }
        else
        {
            var back = PlayerModel.FirstPersonBodyBack * (1f - blend);
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch, back);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                _playerMeshes[part].Render();
            }
            _gl.DepthMask(false);
            var head = player.GetPartMatrix("Head", camera.Position, bodyYaw, camera.Pitch);
            _owner.SetDrawUniforms(head, head * viewProj, blend, light);
            _playerMeshes["Head"].Render();
            _gl.DepthMask(true);
        }

        _owner.BindEntityTextures(mirror);
    }

    // Held-item sizing, in blocks. The sprite is unit-sized and the cube is full-block;
    // both are shrunk to sit in the palm. The grips lift the shape just clear of the
    // forearm with a nudge outward (+X, away from the body) and forward (-Z).
    // RenderManager re-exports them so the item debugger can show and tune the same defaults
    // the game ships.
    internal const float HeldItemScale = 0.5f;
    internal static readonly Vector3 HeldItemGrip = new(0.03f, 0.10f, -0.04f);
    internal const float HeldBlockScale = 0.38f;
    internal static readonly Vector3 HeldBlockGrip = new(0.08f, 0.10f, -0.02f);

    /// <summary>
    /// The held-item model matrix: uniform scale, the item's display transform, the grip nudge,
    /// then the hand anchor. Shared by <see cref="RenderHeldItem"/> and the item debugger so the
    /// preview can never drift from what the game draws. Blocks pass
    /// <see cref="Matrix4x4.Identity"/> as <paramref name="displayTransform"/>: their shape is
    /// the block's own baked model, not a display-configured sprite.
    /// </summary>
    internal static Matrix4x4 PoseHeldItem(Matrix4x4 hand, Matrix4x4 displayTransform, float scale, Vector3 grip) =>
        Matrix4x4.CreateScale(scale) * displayTransform * Matrix4x4.CreateTranslation(grip) * hand;

    /// <summary>
    /// Draws the selected hotbar slot in the right hand: a block as its 3D cube, a pure
    /// item as its sprite posed by the item's display transform (canted tools by default,
    /// the compass facing back up at the holder). Positioned at the fist through the hand
    /// anchor, so it rides the carrying pose, the walk swing and the punch.
    /// </summary>
    internal void RenderHeldItem(Camera camera, PlayerModel player, byte selectedId, bool hasHeld,
        float timeSeconds, float headingDegrees)
    {
        if (camera == null || player == null || selectedId == 0 || !hasHeld) return;

        var backOffset = PlayerModel.FirstPersonBodyBack * (1f - camera.ShoulderBlend);
        var hand = player.GetHandAnchorMatrix(camera.Position, camera.PlayerYaw, backOffset,
            camera.Pitch);
        var viewProj = camera.GetViewProjection();
        // Lit like the body holding it (RenderPlayer samples the same point)
        var light = _owner.LightAt(camera.Position);

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        if (ItemRegistry.IsItem(selectedId))
        {
            if (ItemRegistry.TryGet(selectedId) is not { } type) return;
            var frame = Math.Clamp(type.FrameAt(timeSeconds, headingDegrees), 0,
                Math.Max(0, type.FrameCount - 1));
            if (!_owner.ItemMeshes.TryGetFrame(selectedId, frame, out var mesh, out var texture)) return;

            var model = PoseHeldItem(hand, type.DisplayMatrix(player.HeldItemRotationOffset), HeldItemScale, HeldItemGrip);
            _owner.SetDrawUniforms(model, model * viewProj, 1f, light);

            Units.BindUnit(0, TextureTarget.Texture2DArray, texture);
            mesh.Render();
            _owner.BindWorldTextures();
            return;
        }

        var cube = _owner.BlockCubes.Get(selectedId);
        if (cube == null) return;
        var blockModel = PoseHeldItem(hand, Matrix4x4.Identity, HeldBlockScale, HeldBlockGrip);
        _owner.SetDrawUniforms(blockModel, blockModel * viewProj, 1f, light);
        cube.Render();
    }

    /// <summary>Frees the part meshes and the palette texture, in the order the renderer always
    /// freed them.</summary>
    public void Dispose()
    {
        foreach (var mesh in _playerMeshes.Values)
            mesh.Dispose();
        _playerMeshes.Clear();
        _gl.DeleteTexture(_playerPaletteTex);
    }
}
