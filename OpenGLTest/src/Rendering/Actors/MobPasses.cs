using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Graphics;
using VoxelEngine.Player;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The mob draws: the live slimes from the shared entity mesh and the goblins part by part,
/// with the flint and the torch they hold, for the entity pass and the water mirror. Owns the
/// slime mesh and skin and the goblin rig, its part meshes and its skin, built from their
/// Blockbench projects at load; the world program and the item and block caches stay owned by
/// <see cref="RenderManager"/>. Render thread only.
/// </summary>
internal sealed class MobPasses : IDisposable
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // The slime entity mesh and skin, built from its data-driven Blockbench project at load.
    // Null only when no 'slime' entity kind is registered, in which case the pass is a no-op.
    private Mesh? _slimeMesh;
    private uint _slimeTexture;

    // The goblin rig: one mesh per Blockbench group, posed per frame by each goblin's
    // EntityAnimator. The names are every rig part, ordinal-sorted, so a group the file adds
    // beyond Goblin.RequiredParts draws too; _goblinMeshes is index-aligned with them.
    // Null only when no 'goblin' entity kind is registered, in which case the passes are no-ops.
    private PlayerRig? _goblinRig;
    private string[]? _goblinPartNames;
    private Mesh[]? _goblinMeshes;
    private uint _goblinTexture;
    // The flint item: the stone a goblin holds while it still has ammo.
    private byte _flintId;
    private byte _torchId;

    // Live views of the slime mesh and the goblin rig, for the shadow and mirror passes.
    internal Mesh? SlimeMesh => _slimeMesh;
    internal PlayerRig? GoblinRig => _goblinRig;
    internal string[]? GoblinPartNames => _goblinPartNames;
    internal Mesh[]? GoblinMeshes => _goblinMeshes;
    internal uint GoblinTexture => _goblinTexture;
    internal byte FlintId => _flintId;

    private TextureUnits Units => _owner.Units;

    internal MobPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        if (EntityRegistry.TryGet("slime") is { } slimeType)
        {
            var slimeModel = EntityModel.Load(slimeType.ModelPath);
            _slimeMesh = slimeModel.BuildMesh(gl);
            _slimeTexture = TextureLoader.Upload(gl, slimeModel.TexturePixels,
                slimeModel.TextureWidth, slimeModel.TextureHeight, srgb: true);
        }
        if (EntityRegistry.TryGet("goblin") is { } goblinType)
        {
            // The parse happened once at content load (EntityType.Model); only GL work is left.
            _goblinRig = PlayerRig.FromBbmodel(goblinType.Model);
            var byName = PlayerRig.BuildMeshes(gl, _goblinRig);
            _goblinPartNames = new string[byName.Count];
            byName.Keys.CopyTo(_goblinPartNames, 0);
            Array.Sort(_goblinPartNames, StringComparer.Ordinal);
            _goblinMeshes = new Mesh[_goblinPartNames.Length];
            for (var i = 0; i < _goblinPartNames.Length; i++)
                _goblinMeshes[i] = byName[_goblinPartNames[i]];
            _goblinTexture = TextureLoader.Upload(gl, _goblinRig.TexturePixels,
                _goblinRig.TextureWidth, _goblinRig.TextureHeight, srgb: true);
        }
        _flintId = ContentRegistry.RequireItem("flint").Id;
        _torchId = ContentRegistry.RequireBlock("torch").Id;
    }

    /// <summary>
    /// Draws the live slimes from the shared entity mesh, each squashed/stretched and yawed by
    /// its own state and textured with its own skin. Restores the atlas on unit 0 on the way out
    /// so the passes that follow (the water and cloud passes) see the world textures again.
    /// </summary>
    internal void RenderSlimes(Camera camera, IReadOnlyList<Slime> slimes)
    {
        if (camera == null || _slimeMesh == null || slimes.Count == 0) return;
        DrawSlimes(slimes, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawSlimes(IReadOnlyList<Slime> slimes, in Matrix4x4 viewProj, bool mirror)
    {
        if (_slimeMesh == null) return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _slimeTexture);

        var drewFlash = false;

        foreach (var slime in slimes)
        {
            var model = SlimeTransform(slime);
            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(slime.Position));
            _owner.WorldShader.SetUniform(_owner.LocHitFlash, slime.HitFlash);
            _owner.WorldShader.SetUniform(_owner.LocStatusTint, StatusTintOf(slime));
            _slimeMesh.Render();
            drewFlash = true;
        }

        // uHitFlash and uStatusTint are per-slime values, so put them back rather than tinting
        // whatever draws next.
        if (drewFlash)
        {
            _owner.WorldShader.SetUniform(_owner.LocHitFlash, 0f);
            _owner.WorldShader.SetUniform(_owner.LocStatusTint, Vector4.Zero);
        }

        _owner.BindEntityTextures(mirror);
    }

    // The model's base sits on y = 0 while Position is the body center, so the feet offset lifts
    // it onto the ground; scaling about the origin then squashes it from its base, not its middle.
    // The size scale rides the same scale, so a large slime grows from its feet like its body box.
    internal static Matrix4x4 SlimeTransform(Slime slime) =>
        Matrix4x4.CreateScale(slime.HorizontalScale * slime.SizeScale, slime.VerticalScale * slime.SizeScale,
            slime.HorizontalScale * slime.SizeScale)
        * Matrix4x4.CreateRotationY(slime.Facing)
        * Matrix4x4.CreateTranslation(
            slime.Position.X, slime.Position.Y - slime.BodyHalfHeight, slime.Position.Z);

    /// <summary>
    /// Draws the live goblins part by part, each part posed by the goblin's own
    /// <see cref="EntityAnimator"/> down the rig's parent chain, plus the flint held in the right
    /// hand while it still has stones to throw. Restores the world textures on the way out, like
    /// <see cref="RenderSlimes"/>.
    /// </summary>
    internal void RenderGoblins(Camera camera, IReadOnlyList<Goblin> goblins)
    {
        if (camera == null || _goblinMeshes == null || goblins.Count == 0) return;
        DrawGoblins(goblins, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawGoblins(IReadOnlyList<Goblin> goblins, in Matrix4x4 viewProj, bool mirror)
    {
        if (_goblinMeshes == null || _goblinRig == null || _goblinPartNames == null || goblins.Count == 0)
            return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _goblinTexture);

        foreach (var goblin in goblins)
        {
            var feet = GoblinFeet(goblin);
            var light = _owner.LightAt(goblin.Position);
            var effect = goblin.BodyEffect(feet);
            _owner.WorldShader.SetUniform(_owner.LocHitFlash, goblin.HitFlash);
            _owner.WorldShader.SetUniform(_owner.LocStatusTint, StatusTintOf(goblin));
            for (var i = 0; i < _goblinMeshes.Length; i++)
            {
                var model = goblin.Animation.GetPartMatrix(_goblinPartNames[i], _goblinRig, feet, goblin.Facing) * effect;
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                _goblinMeshes[i].Render();
            }

            if (goblin.Ammo > 0 && _owner.ItemMeshes.TryGet(_flintId, out var stone, out var stoneTexture))
            {
                Units.BindUnit(0, TextureTarget.Texture2DArray, stoneTexture);
                var held = Matrix4x4.CreateScale(Goblin.HeldStoneScale)
                           * goblin.Animation.HandAnchor(Goblin.HandPart, _goblinRig, feet, goblin.Facing) * effect;
                _owner.SetDrawUniforms(held, held * viewProj, 1f, light);
                stone.Render();
                Units.BindUnit(0, TextureTarget.Texture2DArray, _goblinTexture);
            }

            // A lit torch rides the off hand, where the flint rides the right; the world's fire
            // gathering reads the same point (Goblin.TorchFlamePoint)
            if (goblin.TorchLit && _torchId != 0 && _owner.BlockCubes.Get(_torchId) is { } torchCube)
            {
                var off = Matrix4x4.CreateScale(Goblin.HeldTorchScale)
                          * goblin.Animation.HandAnchor(Goblin.OffHandPart, _goblinRig, feet, goblin.Facing) * effect;
                _owner.SetDrawUniforms(off, off * viewProj, 1f, light);
                torchCube.Render();
            }
        }

        // uHitFlash and uStatusTint are per-goblin values, so put them back rather than tinting
        // whatever draws next.
        _owner.WorldShader.SetUniform(_owner.LocHitFlash, 0f);
        _owner.WorldShader.SetUniform(_owner.LocStatusTint, Vector4.Zero);
        _owner.BindEntityTextures(mirror);
    }

    // The uStatusTint value of one mob: its first tinted active effect's linear color at the
    // configured mix weight, or zero (no tint) when none of its effects carries a tint.
    private static Vector4 StatusTintOf(Mob mob) =>
        mob.Effects.TryGetTint(out var tint)
            ? new Vector4(tint, StatusEffectConfig.MobTintStrength)
            : Vector4.Zero;

    // Position is the body center and the model stands on y = 0, so the rig roots at the feet.
    internal static Vector3 GoblinFeet(Goblin goblin) =>
        goblin.Position - new Vector3(0f, Goblin.Height * 0.5f, 0f);

    /// <summary>Frees the slime mesh and skin and the goblin part meshes and skin, in the order
    /// the renderer always freed them.</summary>
    public void Dispose()
    {
        _slimeMesh?.Dispose();
        if (_slimeTexture != 0)
            _gl.DeleteTexture(_slimeTexture);
        if (_goblinMeshes != null)
            foreach (var mesh in _goblinMeshes)
                mesh.Dispose();
        _goblinMeshes = null;
        if (_goblinTexture != 0)
            _gl.DeleteTexture(_goblinTexture);
    }
}
