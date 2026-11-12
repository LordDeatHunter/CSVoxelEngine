using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Graphics;
using VoxelEngine.Player;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The mob draws: the live slimes from the shared entity mesh, the goblins part by part with the
/// flint and the torch they hold, and the realm (hell, sky, tower and cave) mobs part by part with the item their
/// entity file holds (<see cref="EntityType.HeldItemId"/>), for the entity pass and the water mirror. Owns the slime mesh and skin and one <see cref="RigMeshes"/> per clip-animated kind,
/// built from their Blockbench projects at load; the world program and the item and block caches stay owned by
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

    // The goblin rig, posed per frame by each goblin's EntityAnimator. Null only when no 'goblin'
    // entity kind is registered, in which case the passes are no-ops.
    private RigMeshes? _goblin;
    // The realm (hell, sky and tower) mobs' rigs by kind; a kind whose content is not registered has none and is not drawn.
    private readonly Dictionary<EntityType, RigMeshes> _realmRigs = [];
    // The sandworm rig (one mesh per part: Body, Head, Jaw, Tail) drawn once per chain segment; null when the
    // entity is not registered. The part indices are looked up once from its sorted names.
    private RigMeshes? _sandworm;
    private int _wormBody;
    private int _wormHead;
    private int _wormJaw;
    private int _wormTail;
    private readonly List<(Matrix4x4 Model, int Mesh, Vector3 LightAt)> _wormParts = [];
    // The Chunk.CubeFaces index of the -Z face, the side a rig faces
    private const int FrontFace = 0;
    // Armor shells over realm mob parts (a tower skeleton's outfit), built on first use per (kind, part, tint item)
    private readonly Dictionary<(EntityType Type, int Part, ushort Piece), Mesh> _armorShells = [];
    // The flint item: the stone a goblin holds while it still has ammo.
    private ushort _flintId;
    private ushort _torchId;

    // Live views of the slime mesh and the goblin rig, for the shadow and mirror passes.
    internal Mesh? SlimeMesh => _slimeMesh;
    internal RigMeshes? GoblinRig => _goblin;
    internal ushort FlintId => _flintId;

    private TextureUnits Units => _owner.Units;

    // The realm mob kinds a rig is built for, when their content is registered: the hostile ones and every wild animal
    private static readonly string[] RealmRigNames =
        [Hellbrute.EntityName, Cinderwitch.EntityName, Galeraptor.EntityName, Cloudgolem.EntityName, Skeleton.EntityName,
            Skeleton.WarriorEntityName, SkeletonKing.EntityName, Shardling.EntityName, Sporeling.EntityName,
            Magmaborn.EntityName, Spider.EntityName, Spider.BroodmotherEntityName, Scorpion.EntityName,
            .. WildlifeSpecies.Names];

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
        // The parse happened once at content load (EntityType.Model); only GL work is left.
        if (EntityRegistry.TryGet("goblin") is { } goblinType)
            _goblin = RigMeshes.Build(gl, goblinType);
        foreach (var name in RealmRigNames)
            if (EntityRegistry.TryGet(name) is { } realmType)
                _realmRigs[realmType] = RigMeshes.Build(gl, realmType);
        if (EntityRegistry.TryGet(Sandworm.EntityName) is { } wormType)
        {
            _sandworm = RigMeshes.Build(gl, wormType);
            _wormBody = Array.IndexOf(_sandworm.PartNames, "Body");
            _wormHead = Array.IndexOf(_sandworm.PartNames, "Head");
            _wormJaw = Array.IndexOf(_sandworm.PartNames, "Jaw");
            _wormTail = Array.IndexOf(_sandworm.PartNames, "Tail");
            if (_wormBody < 0 || _wormHead < 0 || _wormJaw < 0 || _wormTail < 0)
                throw new InvalidOperationException(
                    $"Entity model '{wormType.ModelPath}' needs the groups Body, Head, Jaw and Tail (tools/generate-sandworm-model.py).");
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
        if (camera == null || _goblin == null || goblins.Count == 0) return;
        DrawGoblins(goblins, camera.GetViewProjection(), mirror: false);
    }

    internal void DrawGoblins(IReadOnlyList<Goblin> goblins, in Matrix4x4 viewProj, bool mirror)
    {
        if (_goblin is not { } rig || goblins.Count == 0)
            return;

        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror);
        Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);

        foreach (var goblin in goblins)
        {
            var feet = goblin.Feet;
            var light = _owner.LightAt(goblin.Position);
            var effect = goblin.BodyEffect(feet);
            DrawRigParts(goblin, rig, viewProj, light, effect);

            if (goblin.Ammo > 0 && _owner.ItemMeshes.TryGet(_flintId, out var stone, out var stoneTexture))
            {
                Units.BindUnit(0, TextureTarget.Texture2DArray, stoneTexture);
                var held = Matrix4x4.CreateScale(Goblin.HeldStoneScale)
                           * goblin.Animation.HandAnchor(Goblin.HandPart, rig.Rig, feet, goblin.Facing) * effect;
                _owner.SetDrawUniforms(held, held * viewProj, 1f, light);
                stone.Render();
                Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
            }

            // A lit torch rides the off hand, where the flint rides the right; the world's fire
            // gathering reads the same point (Goblin.TorchFlamePoint)
            if (goblin.TorchLit && _torchId != 0 && _owner.BlockCubes.Get(_torchId) is { } torchCube)
            {
                var off = Matrix4x4.CreateScale(Goblin.HeldTorchScale)
                          * goblin.Animation.HandAnchor(Goblin.OffHandPart, rig.Rig, feet, goblin.Facing) * effect;
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

    /// <summary>
    /// Draws one realm's live mobs (hell's or the sky's) part by part, each kind with its own rig and skin,
    /// posed by the mob's own animator and body effect. Restores the world textures on the way out, like
    /// <see cref="RenderSlimes"/>.
    /// </summary>
    internal void RenderRealmMobs(Camera camera, IReadOnlyList<RiggedMob> mobs)
    {
        if (camera == null || mobs.Count == 0 || _realmRigs.Count == 0) return;

        var viewProj = camera.GetViewProjection();
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror: false);
        foreach (var mob in mobs)
        {
            if (!_realmRigs.TryGetValue(mob.Type, out var rig)) continue;
            Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
            DrawRigParts(mob, rig, viewProj, _owner.LightAt(mob.Position), mob.BodyEffect(mob.Feet));
        }

        _owner.WorldShader.SetUniform(_owner.LocHitFlash, 0f);
        _owner.WorldShader.SetUniform(_owner.LocStatusTint, Vector4.Zero);
        _owner.BindEntityTextures(mirror: false);
    }

    /// <summary>
    /// Draws the sandworm: every visible chain segment with its part of the model (the head with its jaw, a body
    /// ring, the tail), each lit where it stands, with the hit flash and status tint. Restores the world textures on
    /// the way out like <see cref="RenderSlimes"/>.
    /// </summary>
    internal void RenderSandworms(Camera camera, IReadOnlyList<Sandworm> worms)
    {
        if (camera == null || worms.Count == 0 || _sandworm == null) return;

        var viewProj = camera.GetViewProjection();
        _owner.WorldShader.Use();
        _owner.BindEntityTextures(mirror: false);
        Units.BindUnit(0, TextureTarget.Texture2DArray, _sandworm.Texture);
        foreach (var worm in worms)
        {
            _owner.WorldShader.SetUniform(_owner.LocHitFlash, worm.HitFlash);
            _owner.WorldShader.SetUniform(_owner.LocStatusTint, StatusTintOf(worm));
            GatherSandwormParts(worm);
            foreach (var (model, mesh, lightAt) in _wormParts)
            {
                _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(lightAt));
                _sandworm.Meshes[mesh].Render();
            }
        }

        _owner.WorldShader.SetUniform(_owner.LocHitFlash, 0f);
        _owner.WorldShader.SetUniform(_owner.LocStatusTint, Vector4.Zero);
        _owner.BindEntityTextures(mirror: false);
    }

    /// <summary>The sandworm rig (the shadow pass's lookup), or null when its content is not registered.</summary>
    internal RigMeshes? SandwormRig => _sandworm;

    /// <summary>The parts of <paramref name="worm"/> to draw this frame: the model matrix, the mesh index into
    /// <see cref="SandwormRig"/> and where to sample its light. The list is reused by the next call.</summary>
    internal List<(Matrix4x4 Model, int Mesh, Vector3 LightAt)> GatherSandwormParts(Sandworm worm)
    {
        _wormParts.Clear();
        // A dying worm shrinks a little and sinks into the sand
        var shrink = 1f - 0.4f * worm.DeathProgress;
        var sink = worm.DeathProgress * worm.DeathProgress * 4f;
        for (var i = 0; i < worm.Chain.Count; i++)
        {
            if (!worm.IsVisible(i)) continue;
            var model = SandwormPose.Segment(worm.Chain, i, shrink, sink);
            var lightAt = worm.Chain[i] + new Vector3(0f, Sandworm.SegmentHalfExtent(i) + 0.5f, 0f);
            var mesh = SandwormChain.KindOf(i) switch
            {
                SandwormSegmentKind.Head => _wormHead,
                SandwormSegmentKind.Tail => _wormTail,
                _ => _wormBody,
            };
            _wormParts.Add((model, mesh, lightAt));
            if (i == 0) _wormParts.Add((SandwormPose.Jaw(model, worm.JawOpen), _wormJaw, lightAt));
        }
        return _wormParts;
    }

    /// <summary>The rig of a realm mob kind, or null when its content is not registered (the shadow
    /// pass's lookup).</summary>
    internal RigMeshes? RealmRigOf(EntityType type) => _realmRigs.GetValueOrDefault(type);

    // Every part of one rigged mob with its hit flash and status tint (the caller resets both after
    // its last mob); the rig's skin is already bound on unit 0
    private void DrawRigParts(RiggedMob mob, RigMeshes rig, in Matrix4x4 viewProj, EntityLightSample light,
        in Matrix4x4 effect)
    {
        _owner.WorldShader.SetUniform(_owner.LocHitFlash, mob.HitFlash);
        _owner.WorldShader.SetUniform(_owner.LocStatusTint, StatusTintOf(mob));
        for (var i = 0; i < rig.Meshes.Length; i++)
        {
            var model = rig.PartMatrix(mob, i) * effect;
            _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
            rig.Meshes[i].Render();
            var piece = mob.ArmorPieceOn(rig.PartNames[i]);
            if (piece != 0) DrawArmorShell(mob.Type, rig, i, piece);
        }
        DrawHeldItem(mob, rig, viewProj, light, effect);
    }

    // The item the kind holds (its entity file's held, the skeleton warrior's dagger) at its hand, swinging with
    // that limb, on its own sprite texture with the mob's hit flash and tint still set; the skin goes back after
    private void DrawHeldItem(RiggedMob mob, RigMeshes rig, in Matrix4x4 viewProj, EntityLightSample light,
        in Matrix4x4 effect)
    {
        var type = mob.Type;
        if (type.HeldItemId == 0 || !_owner.ItemMeshes.TryGet(type.HeldItemId, out var mesh, out var texture)) return;
        Units.BindUnit(0, TextureTarget.Texture2DArray, texture);
        var model = type.HeldMatrix(mob.Animation, rig.Rig, mob.Feet, mob.Facing) * effect;
        _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
        mesh.Render();
        Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
    }

    // The armor shell over one already drawn part, with its uniforms still set: the part's cuboids grown a little and
    // tinted by the piece's item color over the white texture (the player's own shells, PlayerPasses.DrawPart)
    private void DrawArmorShell(EntityType type, RigMeshes rig, int part, ushort piece)
    {
        if (!_armorShells.TryGetValue((type, part, piece), out var shell))
        {
            if (!rig.Rig.Parts.TryGetValue(rig.PartNames[part], out var rigPart)) return;
            // A helmet leaves the front (-Z, the model's facing) open, so the skull's face shows through it
            shell = PlayerRig.BuildArmorMesh(_gl, rigPart, PlayerPasses.ArmorInflate, ArmorLook.ColorOf(piece),
                rig.PartNames[part] == "Head" ? FrontFace : -1);
            _armorShells[(type, part, piece)] = shell;
        }
        Units.BindUnit(0, TextureTarget.Texture2DArray, _owner.Players.WhiteTexture);
        shell.Render();
        Units.BindUnit(0, TextureTarget.Texture2DArray, rig.Texture);
    }

    // The uStatusTint value of one mob: its first tinted active effect's linear color at the
    // configured mix weight, or zero (no tint) when none of its effects carries a tint.
    private static Vector4 StatusTintOf(Mob mob) =>
        mob.Effects.TryGetTint(out var tint)
            ? new Vector4(tint, StatusEffectConfig.MobTintStrength)
            : Vector4.Zero;

    /// <summary>Frees the slime mesh and skin, then every rig's part meshes and skin.</summary>
    public void Dispose()
    {
        _slimeMesh?.Dispose();
        if (_slimeTexture != 0)
            _gl.DeleteTexture(_slimeTexture);
        _goblin?.Dispose();
        _goblin = null;
        _sandworm?.Dispose();
        _sandworm = null;
        foreach (var rig in _realmRigs.Values)
            rig.Dispose();
        _realmRigs.Clear();
        foreach (var shell in _armorShells.Values)
            shell.Dispose();
        _armorShells.Clear();
    }
}
