using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
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
using VoxelEngine.Simulation.Ingots;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.Simulation.Stumps;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The station draws of the entity pass: the food cooking on a campfire, every quern's spinning
/// top and waiting input, what a kiln holds (the crucible bowl, its molten pool, the charge
/// still melting, the stored fuel and the ember bed of a lit hearth) and what sits in a mold's pocket. Owns the quern-top and
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

    // The glowing coals on a lit hearth: one flat quad wearing the ember tile, built on first sight. A kiln, an
    // alloy furnace and a bloomery (SmithingPasses) all draw it, scaled to their hearth.
    private Mesh? _emberQuad;

    // A tanning vat's liquid: the still-water tile (plain water) and the mud tile (the brown tanning liquor, once a
    // batch tans; tinting the blue water brown only darkens it to black), each one flat quad built on first sight.
    // SmithingPasses draws them.
    private Mesh? _vatWater;
    private Mesh? _vatLiquor;

    private TextureUnits Units => _owner.Units;

    /// <summary>
    /// The mesh an item or block id draws as in a station pass, binding the texture it samples:
    /// an item's own sprite texture, or the world atlas - a no-op while that is already bound,
    /// so a run of block results never rebinds. Null when the id has no mesh.
    /// </summary>
    internal Mesh? BindStationMesh(ushort id, ref bool atlasBound)
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
    internal void BindAtlas(ref bool atlasBound)
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
            // Food lies face-down on the fire with its bottom toward the fire's center, so each
            // slot's yaw points from its offset back to the middle of the cell.
            var placed = Matrix4x4.CreateScale(scale)
                        * SpritePose.LyingFaceDown(MathF.Atan2(offset.X, offset.Z))
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
    /// Draws every log standing on a stump: the log block's cube squashed to <see cref="StumpConfig.LogWidth"/> by
    /// <see cref="StumpConfig.LogHeight"/>, standing where it was put on the stump's top, turned as placed and, while it
    /// wobbles, tilting about its foot in a ring-down (<see cref="MathUtils.DampedWobble"/>). Skips the shadow pass, like
    /// the quern tops.
    /// </summary>
    internal void RenderStumpLogs(Camera camera, StumpManager stumps)
    {
        if (camera == null || stumps == null || !stumps.HasAny) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (cell, log) in stumps.View())
        {
            var mesh = BindStationMesh(log.LogId, ref atlasBound);
            if (mesh == null) continue;

            var foot = cell + new Vector3(0.5f + log.OffsetX, StumpConfig.StumpTop, 0.5f + log.OffsetZ);
            var progress = 1f - log.WobbleLeft / StumpConfig.WobbleSeconds;
            var tilt = log.WobbleLeft > 0f
                ? StumpConfig.WobbleTilt * MathUtils.DampedWobble(progress, StumpConfig.WobbleCycles)
                : 0f;
            // A log being split shows as two halves that open a little more with every blow; a whole one is one piece
            var block = BlockType.ById[log.LogId];
            var halves = log.SplitHits > 0 ? 2 : 1;
            var gap = log.SplitHits * StumpConfig.SplitGapPerHit;
            var lean = log.SplitHits * StumpConfig.SplitLeanPerHit;
            // A hollow log's tube runs along the model's Z: turned upright it stands on its end
            var upright = block is { StandsOnStump: true };
            var size = upright
                ? new Vector3(StumpConfig.LogWidth, StumpConfig.LogWidth, StumpConfig.LogHeight)
                : new Vector3(StumpConfig.LogWidth, StumpConfig.LogHeight, StumpConfig.LogWidth);
            var stand = upright ? Matrix4x4.CreateRotationX(-MathF.PI / 2f) : Matrix4x4.Identity;
            var place = Matrix4x4.CreateRotationX(tilt) * Matrix4x4.CreateRotationY(log.Yaw) * Matrix4x4.CreateTranslation(foot);
            var light = _owner.LightAt(foot + new Vector3(0f, 0.4f, 0f));

            for (var half = 0; half < halves; half++)
            {
                // The cube is centered on its origin: squash it (to half its width for a half), lift it onto its foot,
                // lean it away from the other half, push it aside, tip it about the foot, turn it
                var side = halves == 1 ? 0f : half == 0 ? -1f : 1f;
                var model = Matrix4x4.CreateScale(halves == 1 ? 1f : 0.5f, 1f, 1f)
                            * Matrix4x4.CreateScale(size)
                            * stand
                            * Matrix4x4.CreateTranslation(0f, StumpConfig.LogHeight / 2f, 0f)
                            * Matrix4x4.CreateRotationZ(-side * lean)
                            * Matrix4x4.CreateTranslation(side * (StumpConfig.LogWidth / 4f + gap / 2f), 0f, 0f)
                            * place;
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                mesh.Render();
            }
        }
    }

    /// <summary>
    /// Draws every ingot pile: each ingot is its metal's cube squashed to ingot size (see <see cref="IngotPileConfig"/>),
    /// three side by side with a gap in each layer, every layer turned 90 degrees from the one below. The chunk meshes
    /// nothing for a pile block. Skips the shadow pass, like the quern tops.
    /// </summary>
    internal void RenderIngotPiles(Camera camera, IngotPileManager piles)
    {
        if (camera == null || piles == null || !piles.HasAny) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (cell, ingots) in piles.View())
        {
            var center = cell + new Vector3(0.5f, 0f, 0.5f);
            var light = _owner.LightAt(cell + new Vector3(0.5f, 0.5f, 0.5f));
            for (var i = 0; i < ingots.Count; i++)
            {
                if (ItemRegistry.TryGet(ingots[i]) is not { IngotBlockId: not 0 } item) continue;
                BindAtlas(ref atlasBound);
                var mesh = _owner.BlockCubes.Get(item.IngotBlockId);
                if (mesh == null) continue;

                var layer = i / IngotPileConfig.PerLayer;
                var slot = i % IngotPileConfig.PerLayer - IngotPileConfig.PerLayer / 2;
                var model = Matrix4x4.CreateScale(IngotPileConfig.IngotLength, IngotPileConfig.IngotHeight, IngotPileConfig.IngotWidth)
                            * Matrix4x4.CreateTranslation(0f, IngotPileConfig.IngotHeight * (layer + 0.5f),
                                slot * (IngotPileConfig.IngotWidth + IngotPileConfig.Gap))
                            * Matrix4x4.CreateRotationY(layer % 2 * MathF.PI / 2f)
                            * Matrix4x4.CreateTranslation(center);
                _owner.SetDrawUniforms(model, model * viewProj, 1f, light);
                mesh.Render();
            }
        }
    }

    // The bone pile's model, built on first sight: it belongs to no block, since a pile is not one
    private Mesh? _bonePileMesh;

    private Mesh BuildBonePileMesh()
    {
        var model = ModelBaker.Bake(ContentRegistry.ModelFiles!.Resolve("block/skeleton_pile"));
        return Primitives.CreateBlockModel(_gl, model);
    }

    // Reused per frame: the tower's bone pile spots
    private readonly List<Vector3> _pileSpots = [];

    /// <summary>
    /// Draws every battle tower bone pile (entities lying at free positions on the floors, not blocks): the skeleton pile
    /// block's model at each spot, turned by a hash of the spot so a floor's piles do not all face one way. Skips the
    /// shadow pass, like the other station draws.
    /// </summary>
    internal void RenderBonePiles(Camera camera, TowerManager towers)
    {
        if (camera == null || towers == null) return;
        _pileSpots.Clear();
        towers.CollectPileSpots(_pileSpots);
        if (_pileSpots.Count == 0) return;

        _owner.WorldShader.Use();
        _owner.BindWorldTextures();
        var mesh = _bonePileMesh ??= BuildBonePileMesh();

        var viewProj = camera.GetViewProjection();
        var maxDistanceSquared = BonePileDrawDistance * BonePileDrawDistance;
        foreach (var spot in _pileSpots)
        {
            if (Vector3.DistanceSquared(spot, camera.Position) > maxDistanceSquared) continue;
            var yaw = (uint)(spot.X * 1031f + spot.Z * 2053f) % 628u / 100f;
            var model = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(spot + new Vector3(0f, 0.5f, 0f));
            _owner.SetDrawUniforms(model, model * viewProj, 1f, _owner.LightAt(spot + new Vector3(0f, 0.5f, 0f)));
            mesh.Render();
        }
    }

    private const float BonePileDrawDistance = 96f;

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
    /// Draws the ember bed of a lit hearth: ember quads laid side by side over <paramref name="size"/> (across
    /// the opening, then into it; <see cref="KilnRenderer.EmberGrid"/> keeps each near
    /// <see cref="KilnRenderer.EmberTileSize"/>, so a wide hearth is not one stretched tile), turned to the
    /// structure's <paramref name="yaw"/> and centered on <paramref name="center"/>, self-lit and faded to
    /// <paramref name="alpha"/>. The world atlas must be bound.
    /// </summary>
    internal void DrawEmberBed(Vector3 center, Vector2 size, float yaw, float alpha, Matrix4x4 viewProj)
    {
        _emberQuad ??= Primitives.CreateFlatQuad(_gl, Rgba.FromFloat(1f, 1f, 1f, 1f),
            UvRect.Tile(TextureAtlas.TileOf("ember_bed")));
        var (columns, rows) = KilnRenderer.EmberGrid(size);
        var cell = new Vector2(size.X / columns, size.Y / rows);
        var place = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(center);
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var model = Matrix4x4.CreateScale(cell.X, 1f, cell.Y)
                        * Matrix4x4.CreateTranslation((column + 0.5f) * cell.X - size.X * 0.5f, 0f,
                            (row + 0.5f) * cell.Y - size.Y * 0.5f)
                        * place;
            _owner.SetDrawUniforms(model, model * viewProj, alpha, EntityLightSample.SelfLit);
            _emberQuad.Render();
        }
    }

    /// <summary>
    /// Draws what a kiln holds inside its opening: the crucible bowl, the molten pool rising in
    /// it, the lump of solid charge still waiting to melt over its rim, and the stored fuel
    /// lying on the hearth. The pool's height comes from
    /// <see cref="KilnManager.DisplayMoltenMl"/>, which carries the fraction of the melt tick in
    /// flight, so the surface rises smoothly instead of stepping once per chunk melted; the lump
    /// is its complement (<see cref="KilnManager.DisplayPendingMl"/> through
    /// <see cref="KilnRenderer.PendingStack"/>), so what the pool gains the top lump loses. The pool
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
            var layout = state.Profile.Layout;

            // One sample per kiln, at its mouth (KilnLayout.MouthLocal)
            var light = _owner.LightAt(layout.ToWorld(anchor, facing, layout.MouthLocal));

            if (state.Lit)
                DrawEmberBed(layout.ToWorld(anchor, facing, layout.EmberLocal), layout.EmberSize,
                    KilnRenderer.OpeningYaw(facing), 1f, viewProj);

            if (state.HasCrucible)
            {
                // CreateBlockModel centers a cell's model on the origin, so lift by half a block
                // to put the bowl's floor back on the hearth. The bowl is symmetric: no yaw.
                var bowl = layout.ToWorld(anchor, facing, layout.CrucibleLocal);
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
                    // One piece per unit of the last thing charged in, stacked over the rim and
                    // facing the opening; only the top one, the unit melting, shrinks into the
                    // pool below
                    var (count, lastScale) = KilnRenderer.PendingStack(pendingMl, KilnManager.PendingUnitMl(state));
                    var drawn = Math.Min(count, KilnRenderer.MaxPendingLumps);
                    var foot = layout.ToWorld(anchor, facing, layout.PendingLocal);
                    var lumpYaw = KilnRenderer.OpeningYaw(facing);
                    var rise = 0f;
                    var flat = ItemRegistry.IsItem(inputId);
                    for (var i = 0; i < drawn; i++)
                    {
                        var size = KilnRenderer.PendingSize * (i == drawn - 1 ? lastScale : 1f);
                        // Item sprites lie flat, face-up, in a tight pile; blocks stay cubes stacked on each other
                        var placed = flat
                            ? Matrix4x4.CreateScale(size)
                                * SpritePose.LyingFaceUp(lumpYaw + i * 0.35f)
                                * Matrix4x4.CreateTranslation(foot + new Vector3(0f, rise + SpritePose.RestingLift(size), 0f))
                            : Matrix4x4.CreateScale(size)
                                * Matrix4x4.CreateRotationY(lumpYaw + i * 0.6f)
                                * Matrix4x4.CreateTranslation(foot + new Vector3(0f, rise + size * 0.5f, 0f));
                        _owner.SetDrawUniforms(placed, placed * viewProj, 1f, light);
                        lump.Render();
                        rise += flat
                            ? SpritePose.HalfThickness(size, SpritePose.ItemSpriteFrame) * 2f + SpritePose.RestingClearance
                            : size * KilnRenderer.PendingStackStep;
                    }
                }
            }

            if (state.Fuel <= 0 || state.FuelItemId == 0) continue;

            var fuelIsItem = ItemRegistry.IsItem(state.FuelItemId);
            var fuelMesh = BindStationMesh(state.FuelItemId, ref atlasBound);
            if (fuelMesh == null) continue;

            // The stored units lie in rows on the hearth floor (KilnLayout.FuelSlotLocal); sprites lie face-up
            // on it, cubes stand on it, both turned to the opening.
            var yaw = KilnRenderer.OpeningYaw(facing);
            var orient = fuelIsItem
                ? SpritePose.LyingFaceUp(yaw)
                : Matrix4x4.CreateRotationY(yaw);
            var lift = fuelIsItem
                ? SpritePose.RestingLift(KilnRenderer.FuelSize)
                : KilnRenderer.FuelSize * 0.5f;
            for (var i = 0; i < state.Fuel; i++)
            {
                var placed = Matrix4x4.CreateScale(KilnRenderer.FuelSize)
                            * orient
                            * Matrix4x4.CreateTranslation(
                                layout.ToWorld(anchor, facing, layout.FuelSlotLocal(i, state.Fuel))
                                + new Vector3(0f, lift, 0f));
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

    /// <summary>The flat liquid quad of a tanning vat: plain water, or the brown tanning liquor once its batch tans.
    /// Built on first sight and kept for the run; the world atlas must be bound.</summary>
    internal Mesh VatLiquid(bool liquor)
    {
        if (liquor)
            return _vatLiquor ??= LiquidQuad("mud");
        return _vatWater ??= LiquidQuad("water_still");
    }

    private Mesh LiquidQuad(string tile) =>
        Primitives.CreateFlatQuad(_gl, Rgba.FromFloat(1f, 1f, 1f, 1f), UvRect.Tile(TextureAtlas.TileOf(tile)));

    /// <summary>Frees the quern top, the crucible bowl, the ember quad, the vat liquids and every molten quad built so
    /// far, in the order the renderer always freed them.</summary>
    public void Dispose()
    {
        _quernTop?.Dispose();
        _crucibleMesh?.Dispose();
        _bonePileMesh?.Dispose();
        _emberQuad?.Dispose();
        _vatWater?.Dispose();
        _vatLiquor?.Dispose();
        foreach (var quad in _moltenQuads.Values)
            quad.Dispose();
        _moltenQuads.Clear();
    }
}
