using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Models;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Networking.Client;
using VoxelEngine.Particles;
using VoxelEngine.Config;
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
    private uint _whiteTex;
    private Dictionary<string, Mesh> _playerMeshes = new();

    // Armor shells, built on first use per (part, piece): the part's cuboids grown a little and tinted by the piece
    private readonly Dictionary<(string Part, byte Piece), Mesh> _armorMeshes = new();

    /// <summary>How far an armor shell stands off the body, in blocks.</summary>
    private const float ArmorInflate = 0.03f;

    /// <summary>The armor the local player wears, set once a frame by the world renderer; the mirror pass reads it too.</summary>
    internal ArmorLook LocalArmor { get; set; } = ArmorLook.None;

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
        _whiteTex = TextureLoader.Upload(gl, [255, 255, 255, 255], 1, 1, srgb: true);
    }

    /// <summary>Draws one body part's mesh and then, when <paramref name="armor"/> covers it, the armor shell over it with the same
    /// uniforms already set. Puts the skin texture back after a shell.</summary>
    private void DrawPart(string part, ArmorLook armor)
    {
        _playerMeshes[part].Render();
        if (armor.IsNone) return;

        for (var slot = 0; slot < GearConfig.ArmorSlotCount; slot++)
        {
            var piece = armor.PieceAt(slot);
            if (piece == 0 || Array.IndexOf(ArmorLook.PartsOf(slot), part) < 0) continue;
            if (!_armorMeshes.TryGetValue((part, piece), out var shell))
            {
                if (!PlayerRig.Parts.TryGetValue(part, out var rigPart)) return;
                shell = PlayerRig.BuildArmorMesh(_gl, rigPart, ArmorInflate, ArmorLook.ColorOf(piece));
                _armorMeshes[(part, piece)] = shell;
            }
            Units.BindUnit(0, TextureTarget.Texture2DArray, _whiteTex);
            shell.Render();
            Units.BindUnit(0, TextureTarget.Texture2DArray, _playerPaletteTex);
        }
    }

    /// <summary>
    /// Draws every remote player as the player body posed at their broadcast eye, using the client's
    /// own player rig and textures so all players look alike. A remote's held item is not on the
    /// wire, so while one swings a blade the client draws the first item with that attack pattern
    /// (<see cref="SliceTrailSystem.AssumedBlade"/>) in its hand: the same blade its slice trail is
    /// built from. Restores the world textures on the way out, like <see cref="MobPasses.RenderGoblins"/>.
    /// </summary>
    internal void RenderRemotePlayers(Camera camera, IReadOnlyList<RemotePlayer> players)
    {
        if (camera == null || _playerMeshes.Count == 0 || players.Count == 0)
            return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(false);
        var viewProj = camera.GetViewProjection();
        foreach (var remote in players)
        {
            // Bound per remote: the previous remote's blade left its item texture on unit 0
            Units.BindUnit(0, TextureTarget.Texture2DArray, _playerPaletteTex);
            var light = _owner.LightAt(remote.Eye);
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = remote.Model.GetPartMatrix(part, remote.Eye, remote.Yaw, remote.Pitch);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                DrawPart(part, remote.Armor);
            }

            var head = remote.Model.GetPartMatrix("Head", remote.Eye, remote.Yaw, remote.Pitch);
            _owner.SetDrawUniforms(head, head * viewProj, 1f, light);
            DrawPart("Head", remote.Armor);

            DrawRemoteBlade(remote, viewProj, light);
        }

        _owner.BindEntityTextures(false);
    }

    /// <summary>
    /// Draws the blade a remote is assumed to hold while it swings one (nothing otherwise), through
    /// the same <see cref="DrawItemSprite"/> the local held item uses, then puts back the entity
    /// textures the remote bodies draw with.
    /// </summary>
    private void DrawRemoteBlade(RemotePlayer remote, in Matrix4x4 viewProj, EntityLightSample light)
    {
        if (SliceTrailSystem.AssumedBlade(remote.Model) is not { } item) return;

        var model = HeldItemPose.SpriteMatrix(remote.Model, item, remote.Eye, remote.Yaw, remote.Pitch, 0f);
        DrawItemSprite(item, 0, model, viewProj, light);
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

        var armor = LocalArmor;
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
                DrawPart(part, armor);
            }
        }
        else if (blend >= 1f)
        {
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                DrawPart(part, armor);
            }
            var head = player.GetPartMatrix("Head", camera.Position, bodyYaw, camera.Pitch);
            _owner.SetDrawUniforms(head, head * viewProj, 1f, light);
            DrawPart("Head", armor);
        }
        else
        {
            var back = PlayerModel.FirstPersonBodyBack * (1f - blend);
            foreach (var part in PlayerModel.BodyParts)
            {
                var model = player.GetPartMatrix(part, camera.Position, bodyYaw, camera.Pitch, back);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                DrawPart(part, armor);
            }
            _gl.DepthMask(false);
            var head = player.GetPartMatrix("Head", camera.Position, bodyYaw, camera.Pitch);
            _owner.SetDrawUniforms(head, head * viewProj, blend, light);
            DrawPart("Head", armor);
            _gl.DepthMask(true);
        }

        _owner.BindEntityTextures(mirror);
    }

    /// <summary>
    /// Draws the selected hotbar slot in the right hand: a block as its 3D cube, a pure
    /// item as its sprite posed by the item's display transform (canted tools by default,
    /// the compass facing back up at the holder). Positioned at the fist through the hand
    /// anchor, so it rides the carrying pose, the walk swing and the punch. The sizing and the
    /// sprite's matrix are <see cref="HeldItemPose"/>'s, shared with the slice trail and the item
    /// debugger.
    /// </summary>
    internal void RenderHeldItem(Camera camera, PlayerModel player, byte selectedId, bool hasHeld,
        float timeSeconds, float headingDegrees)
    {
        if (camera == null || player == null || selectedId == 0 || !hasHeld) return;

        var backOffset = HeldItemPose.BackOffset(camera.ShoulderBlend);
        var viewProj = camera.GetViewProjection();
        // Lit like the body holding it (RenderPlayer samples the same point)
        var light = _owner.LightAt(camera.Position);

        _owner.WorldShader.Use();

        if (ItemRegistry.IsItem(selectedId))
        {
            if (ItemRegistry.TryGet(selectedId) is not { } type) return;
            var frame = Math.Clamp(type.FrameAt(timeSeconds, headingDegrees), 0,
                Math.Max(0, type.FrameCount - 1));
            var model = HeldItemPose.SpriteMatrix(player, type, camera.Position, camera.PlayerYaw,
                camera.Pitch, backOffset);
            if (!DrawItemSprite(type, frame, model, viewProj, light)) return;
            _owner.BindWorldTextures();
            return;
        }

        _owner.BindWorldTextures();
        var cube = _owner.BlockCubes.Get(selectedId);
        if (cube == null) return;
        var hand = player.GetHandAnchorMatrix(camera.Position, camera.PlayerYaw, backOffset,
            camera.Pitch);
        var blockModel = HeldItemPose.Pose(hand, Matrix4x4.Identity, HeldItemPose.HeldBlockScale,
            HeldItemPose.HeldBlockGrip);
        _owner.SetDrawUniforms(blockModel, blockModel * viewProj, 1f, light);
        cube.Render();
    }

    /// <summary>
    /// Draws one frame of an item's extruded sprite with <paramref name="model"/> through the bound
    /// world program: the world textures, the draw uniforms, the frame's texture on unit 0, the mesh.
    /// Leaves the item texture on unit 0; restoring the caller's textures is the caller's job. False
    /// when the item has no such frame, before anything is bound but the world textures.
    /// </summary>
    private bool DrawItemSprite(Item type, int frame, Matrix4x4 model, Matrix4x4 viewProj, EntityLightSample light)
    {
        _owner.BindWorldTextures();
        if (!_owner.ItemMeshes.TryGetFrame(type.Id, frame, out var mesh, out var texture)) return false;

        _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
        Units.BindUnit(0, TextureTarget.Texture2DArray, texture);
        mesh.Render();
        return true;
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
