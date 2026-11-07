using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The station draws of the entity pass: the food cooking on a campfire, every quern's spinning
/// top and waiting input, what a kiln holds (the crucible bowl, its molten pool, the charge
/// still melting and the stored fuel) and what sits in a mold's pocket. Owns the quern-top and
/// crucible meshes and the molten quads it draws; the world program and the item and block
/// caches stay owned by <see cref="RenderManager"/>. Render thread only.
/// </summary>
internal sealed class StationPasses : IDisposable
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    // The quern's spinning plate and handle, built from the top quads of its baked model
    // (see QuernRenderer): the chunk only meshes the base, so this entity completes it.
    private Mesh? _quernTop;

    // The crucible bowl standing in a kiln's opening, built from the crucible item's baked model
    // (see CrucibleItem): it is an item, so nothing meshes it into a chunk.
    private Mesh? _crucibleMesh;

    // One flat liquid quad per molten material, tinted with the material's color and built on
    // first sight. Both the crucible's pool and a mold's fill draw it, scaled and lifted.
    private readonly Dictionary<byte, Mesh> _moltenQuads = new();

    private TextureUnits Units => _owner.Units;

    /// <summary>
    /// The mesh an item or block id draws as in a station pass, binding the texture it samples:
    /// an item's own sprite texture, or the world atlas - a no-op while that is already bound,
    /// so a run of block results never rebinds. Null when the id has no mesh.
    /// </summary>
    private Mesh? BindStationMesh(byte id, ref bool atlasBound)
    {
        if (ItemRegistry.IsItem(id))
        {
            if (!_owner.ItemMeshes.TryGet(id, out var itemMesh, out var itemTexture)) return null;
            Units.BindUnit(0, TextureTarget.Texture2DArray, itemTexture);
            atlasBound = false;
            return itemMesh;
        }

        BindAtlas(ref atlasBound);
        return _owner.BlockCubes.Get(id);
    }

    /// <summary>Rebinds the world atlas after an item mesh left its own texture on unit 0.</summary>
    private void BindAtlas(ref bool atlasBound)
    {
        if (atlasBound) return;
        _owner.BindWorldTextures();
        atlasBound = true;
    }

    internal StationPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
        _quernTop = BuildQuernTopMesh();
        _crucibleMesh = BuildCrucibleMesh();
    }

    /// <summary>
    /// The quern's spinning top as a standalone mesh: the plate and handle quads above the
    /// air gap of its baked model (see <see cref="QuernRenderer"/>), centered on the origin
    /// so the render pass can yaw it about the cell center. Like the other content errors,
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
        return Primitives.CreateBlockModel(_gl, top);
    }

    internal void RenderCampfireCooking(Camera camera, CampfireManager campfires)
    {
        if (camera == null || campfires == null) return;

        var drew = false;
        var atlasBound = true;
        var viewProj = camera.GetViewProjection();
        foreach (var (cell, slot, itemId, _, _) in campfires.CookingView())
        {
            if (!drew)
            {
                _owner.WorldShader.Use();
                _owner.BindWorldTextures();
                drew = true;
            }

            var mesh = BindStationMesh(itemId, ref atlasBound);
            if (mesh == null) continue;

            var scale = ItemDrop.Size * (ItemRegistry.IsItem(itemId) ? 1.5f : 1f);
            var offset = CampfireManager.SlotOffsets[slot % CampfireManager.SlotOffsets.Length];
            var placed = Matrix4x4.CreateScale(scale)
                        * Matrix4x4.CreateRotationY(slot * MathF.PI / 2f)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f, 0f, 0.5f) + offset);

            _owner.SetDrawUniforms(placed, placed * viewProj, 1f, _owner.LightAt(cell + new Vector3(0.5f)));
            mesh.Render();
        }
    }

    /// <summary>
    /// Draws every touched quern's spinning top yawed about its cell center, plus its waiting
    /// input hovering over the plate. The chunk holds only the base (see
    /// <see cref="QuernRenderer"/>), so this entity is what completes the quern — at rest
    /// angle it sits exactly where the static model would. Tops skip the shadow pass, like
    /// the campfire food below.
    /// </summary>
    internal void RenderQuerns(Camera camera, QuernManager querns)
    {
        if (camera == null || querns == null || !querns.HasAny || _quernTop == null) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (cell, angle, inputId) in querns.QuernView())
        {
            // The top mesh is centered on its own origin, so it yaws in place
            var center = cell + new Vector3(0.5f, 0.5f, 0.5f);
            var model = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(center);
            // One sample per quern, shared by the plate and the input hovering over it
            var light = _owner.LightAt(center);
            _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
            _quernTop.Render();

            if (inputId == 0) continue;

            var mesh = BindStationMesh(inputId, ref atlasBound);
            if (mesh == null) continue;

            // Hovering over the plate, clear of the handle's orbit
            var scale = ItemDrop.Size * (ItemRegistry.IsItem(inputId) ? 1.5f : 1f);
            var placed = Matrix4x4.CreateScale(scale)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.35f, 0.82f, 0.35f));
            _owner.SetDrawUniforms(placed, placed * viewProj, 1f, light);
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
        return Primitives.CreateBlockModel(_gl, model);
    }

    /// <summary>
    /// The flat liquid quad of one molten material, built on first sight and kept for the run.
    /// Every pool of that material — in a crucible or in a mold — draws this one mesh.
    /// </summary>
    private Mesh MoltenQuad(MoltenMaterial material)
    {
        if (_moltenQuads.TryGetValue(material.Id, out var mesh)) return mesh;
        // The tint is authored in sRGB and multiplies the decoded lava tile, so decode it too
        var c = material.Color.ToVector4();
        var lin = ColorSpace.SrgbToLinear(new Vector3(c.X, c.Y, c.Z));
        mesh = Primitives.CreateFlatQuad(_gl, Rgba.FromFloat(lin.X, lin.Y, lin.Z, c.W), UvRect.Tile(material.Tile));
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
    /// <see cref="KilnRenderer.PendingScale01"/>), so what the pool gains the lump loses. The pool
    /// holds every molten metal together, tinted as <see cref="KilnManager.PoolMaterial"/>.
    /// </summary>
    internal void RenderKilns(Camera camera, KilnManager kilns)
    {
        if (camera == null || kilns == null || !kilns.HasAny || _crucibleMesh == null) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (anchor, facing, state) in kilns.KilnView())
        {
            BindAtlas(ref atlasBound);

            // One sample per kiln, half a block outside its opening: every cell inside the
            // structure is solid brickwork, so the light the hearth sees is the light at its mouth.
            var light = _owner.LightAt(KilnRenderer.ToWorld(anchor, facing, new Vector3(1f, 0.5f, -0.5f)));

            if (state.HasCrucible)
            {
                // CreateBlockModel centers a cell's model on the origin, so lift by half a block
                // to put the bowl's floor back on the hearth. The bowl is symmetric: no yaw.
                var bowl = KilnRenderer.ToWorld(anchor, facing, KilnRenderer.CrucibleLocal);
                var model = Matrix4x4.CreateTranslation(bowl + new Vector3(0f, 0.5f, 0f));
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                _crucibleMesh.Render();

                var ml = KilnManager.DisplayMoltenMl(state);
                if (ml > 0f && ContentRegistry.MoltenMaterialById(KilnManager.PoolMaterial(state)) is { } material)
                {
                    var liquid = Matrix4x4.CreateScale(KilnRenderer.CrucibleInnerMax - KilnRenderer.CrucibleInnerMin)
                                * Matrix4x4.CreateTranslation(bowl + new Vector3(0f,
                                    KilnRenderer.LiquidHeight(ml, KilnManager.CrucibleCapacityOf(state)), 0f));
                    // The molten pool glows on its own, as the flat quad's baked light always meant
                    _owner.SetDrawUniforms(liquid, liquid * viewProj, 1f, EntityLightSample.SelfLit);
                    MoltenQuad(material).Render();
                }

                var pendingMl = KilnManager.DisplayPendingMl(state);
                var inputId = state.Contents.PendingInputId;
                if (pendingMl > 0f && inputId != 0
                    && BindStationMesh(inputId, ref atlasBound) is { } lump)
                {
                    // One piece of the last thing charged in, resting over the rim and facing
                    // the opening; it shrinks as its last unit melts into the pool below
                    var size = KilnRenderer.PendingSize
                             * KilnRenderer.PendingScale01(pendingMl, KilnManager.PendingUnitMl(state));
                    var foot = KilnRenderer.ToWorld(anchor, facing, KilnRenderer.PendingLocal);
                    var placed = Matrix4x4.CreateScale(size)
                                * Matrix4x4.CreateRotationY(KilnRenderer.OpeningYaw(facing))
                                * Matrix4x4.CreateTranslation(foot + new Vector3(0f, size * 0.5f, 0f));
                    _owner.SetDrawUniforms(placed, placed * viewProj, 1f, light);
                    lump.Render();
                }
            }

            if (state.Fuel <= 0 || state.FuelItemId == 0) continue;

            var fuelIsItem = ItemRegistry.IsItem(state.FuelItemId);
            var fuelMesh = BindStationMesh(state.FuelItemId, ref atlasBound);
            if (fuelMesh == null) continue;

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
                _owner.SetDrawUniforms(placed, placed * viewProj, 1f, light);
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
    internal void RenderCastings(Camera camera, CastingManager castings)
    {
        if (camera == null || castings == null || !castings.HasAny) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (cell, materialId, fill01, cool01, done, resultId) in castings.CastingView())
        {
            var resultIsItem = ItemRegistry.IsItem(resultId);
            var pocket = CastingRenderer.PocketFor(!resultIsItem);
            if (done || cool01 > 0f)
            {
                var mesh = BindStationMesh(resultId, ref atlasBound);
                if (mesh == null) continue;

                // The cast lies face-up in the pocket, filling it; a block result is a cube
                // fitted to its basin
                var sprite = resultIsItem
                    ? Matrix4x4.CreateScale(CastingRenderer.ResultSize)
                      * SpritePose.LyingFaceUp(0f)
                      * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f, CastingRenderer.ResultY, 0.5f))
                    : Matrix4x4.CreateScale(pocket.ResultCubeSize)
                      * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f, pocket.ResultCubeY, 0.5f));

                _owner.SetDrawUniforms(sprite, sprite * viewProj, done ? 1f : CastingRenderer.ResultAlpha(cool01),
                    _owner.LightAt(cell + new Vector3(0.5f)));
                mesh.Render();
            }

            if (done || fill01 <= 0f) continue;
            if (ContentRegistry.MoltenMaterialById(materialId) is not { } material) continue;

            BindAtlas(ref atlasBound);
            var liquid = Matrix4x4.CreateScale(pocket.Width)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f,
                            pocket.LiquidHeightAt(fill01), 0.5f));
            // Self-lit, like the kiln's pool: molten metal is its own light source
            _owner.SetDrawUniforms(liquid, liquid * viewProj, CastingRenderer.LiquidAlpha(cool01), EntityLightSample.SelfLit);
            MoltenQuad(material).Render();
        }

        // Leave the shared alpha uniform where every other entity pass expects it
        _owner.WorldShader.SetUniform(_owner.LocAlpha, 1f);
    }

    /// <summary>Frees the quern top, the crucible bowl and every molten quad built so far, in
    /// the order the renderer always freed them.</summary>
    public void Dispose()
    {
        _quernTop?.Dispose();
        _crucibleMesh?.Dispose();
        foreach (var quad in _moltenQuads.Values)
            quad.Dispose();
        _moltenQuads.Clear();
    }
}
