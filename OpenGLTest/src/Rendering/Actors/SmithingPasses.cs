using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Stations;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Leatherwork;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.Simulation.WorkStations;

namespace VoxelEngine.Rendering.Actors;

/// <summary>
/// The iron works' draws of the entity pass: what a bloomery holds (the ore charge in its throat, the fuel bed and
/// the finished blooms in its tapping arch, and the glow of its hearth and throat, which follows the fire's heat)
/// what lies on a work station (an anvil's metal, a fleshing beam's hides), what is in a tanning vat (its water or
/// liquor, the hides soaking in it, the leather left when it is done), and every bellows that is pressed down, squashed to its height. Where a bloomery's props lie is <see cref="BloomeryLayout"/>. Owns no GL
/// resource: the meshes, the ember quad and the texture binds are <see cref="StationPasses"/>'s. Render thread only.
/// </summary>
internal sealed class SmithingPasses(RenderManager owner, StationPasses stations)
{
    private const float PieceSize = KilnRenderer.FuelSize;
    private const float WorkPieceSize = 0.36f;
    // How far apart along x a station's stack and its finished results lie when both are on it
    private const float WorkPileSpread = 0.2f;
    // A tanning vat's inside: the liquid's width and the floor the finished leather lies on
    private const float VatInnerWidth = 12f / 16f;
    private const float VatFloorY = 2f / 16f;
    // Radians each piece of a pile turns against the one below, so the pile reads as separate pieces
    private const float StackTwist = 0.18f;

    /// <summary>
    /// Draws every bloomery's props and every work station's stack. Nothing is drawn for the station or the shaft
    /// themselves: they are the block's own model.
    /// </summary>
    internal void RenderSmithing(Camera camera, SmithingStations smithing)
    {
        if (camera == null || smithing == null) return;
        if (!smithing.HasAny && smithing.Bellows.Active.Count == 0) return;

        owner.WorldShader.Use();
        owner.BindWorldTextures();

        var viewProj = camera.GetViewProjection();
        var atlasBound = true;

        foreach (var (anchor, facing, state) in smithing.Bloomeries.View())
            DrawBloomery(anchor, facing, state, viewProj, ref atlasBound);

        foreach (var (cell, state) in smithing.WorkStations.View())
            DrawWorkStation(cell, state, viewProj, ref atlasBound);

        foreach (var (cell, state) in smithing.Vats.View())
            DrawVat(cell, state, viewProj, ref atlasBound);

        // A pressed bellows: its block's cube squashed to its height, standing on the cell's floor (the chunk
        // mesh leaves the cell out while it is not a full block, see BellowsSquash)
        foreach (var (cell, id, height) in smithing.Bellows.Active)
        {
            stations.BindAtlas(ref atlasBound);
            if (owner.BlockCubes.Get(id) is not { } cube) continue;
            var model = Matrix4x4.CreateScale(1f, height, 1f)
                        * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f, height * 0.5f, 0.5f));
            owner.SetDrawUniforms(model, model * viewProj, 1f, owner.LightAt(cell + new Vector3(0.5f, 1.2f, 0.5f)));
            cube.Render();
        }

        // Leave the shared alpha uniform where every other entity pass expects it
        owner.WorldShader.SetUniform(owner.LocAlpha, 1f);
    }

    private void DrawBloomery(Vector3 anchor, int facing, BloomeryManager.BloomeryState state, Matrix4x4 viewProj,
        ref bool atlasBound)
    {
        var light = owner.LightAt(BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.MouthLocal));
        var yaw = KilnRenderer.OpeningYaw(facing);
        // Blooms fresh out of a fire that is still hot glow by themselves
        var hot = state.Lit && BloomeryRules.Smelts(state.Heat);

        if (state.Lit)
        {
            stations.BindAtlas(ref atlasBound);
            var glow = BloomeryRules.GlowAlpha(state.Heat);
            stations.DrawEmberBed(BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.HearthEmberLocal),
                BloomeryLayout.HearthEmberSize, yaw, glow, viewProj);
            stations.DrawEmberBed(BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.ThroatEmberLocal),
                new Vector2(BloomeryLayout.ThroatEmberSize), yaw, glow, viewProj);
        }

        DrawFuelBed(anchor, facing, state, yaw, light, viewProj, ref atlasBound);

        var (charge, topScale) = BloomeryLayout.ChargeStack(state.Ore,
            state.UnitProgress / Config.BloomeryConfig.SecondsPerOre);
        DrawPile(state.Profile.OreId, charge, topScale,
            BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.ChargeLocal), yaw, PieceSize, light, viewProj,
            ref atlasBound);

        DrawPile(state.Profile.BloomId, state.Blooms, 1f,
            BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.BloomLocal), yaw, PieceSize,
            hot ? EntityLightSample.SelfLit : light, viewProj, ref atlasBound);
    }

    // The stored fuel as a bed on the hearth floor (BloomeryLayout.FuelSlotLocal): sprites lie face-up on it,
    // cubes stand on it, both turned to the arch
    private void DrawFuelBed(Vector3 anchor, int facing, BloomeryManager.BloomeryState state, float yaw,
        EntityLightSample light, Matrix4x4 viewProj, ref bool atlasBound)
    {
        if (state.Fuel <= 0 || state.FuelItemId == 0) return;
        if (stations.BindStationMesh(state.FuelItemId, ref atlasBound) is not { } mesh) return;

        var isItem = ItemRegistry.IsItem(state.FuelItemId);
        var orient = isItem ? SpritePose.LyingFaceUp(yaw) : Matrix4x4.CreateRotationY(yaw);
        var lift = isItem ? SpritePose.RestingLift(PieceSize) : PieceSize * 0.5f;
        for (var i = 0; i < state.Fuel; i++)
        {
            var placed = Matrix4x4.CreateScale(PieceSize)
                         * orient
                         * Matrix4x4.CreateTranslation(
                             BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.FuelSlotLocal(i, state.Fuel))
                             + new Vector3(0f, lift, 0f));
            owner.SetDrawUniforms(placed, placed * viewProj, 1f, light);
            mesh.Render();
        }
    }

    // A pile of `count` pieces of `id` standing on `foot`: each rests on the one below and is turned a little
    // against it, and the top one is drawn at `topScale` of its size (the unit a bloomery is smelting away). An
    // item is a face-up sprite, a block a cube.
    // A work station's stack on its surface, and beside it the finished results waiting for their collect item
    private void DrawWorkStation(Vector3 cell, WorkStationManager.State state, Matrix4x4 viewProj, ref bool atlasBound)
    {
        if (state.IsEmpty || BlockType.ById[state.BlockId]?.WorkStation is not { } profile) return;
        var light = owner.LightAt(cell + new Vector3(0.5f, 1.2f, 0.5f));
        var spread = state.InputCount > 0 && state.FinishedCount > 0 ? WorkPileSpread : 0f;
        var surface = cell + new Vector3(0.5f, profile.SurfaceY, 0.5f);
        if (state.InputCount > 0)
            DrawPile(state.InputId, state.InputCount, 1f, surface + new Vector3(spread, 0f, 0f), 0f, WorkPieceSize, light,
                viewProj, ref atlasBound);
        if (state.FinishedCount > 0)
            DrawPile(state.FinishedId, state.FinishedCount, 1f, surface - new Vector3(spread, 0f, 0f), 0f, WorkPieceSize,
                light, viewProj, ref atlasBound);
    }

    // A vat's liquid at its profile's height, the batch's hides floating in it, and the leather on its floor once done
    private void DrawVat(Vector3 cell, TanningVatManager.VatState state, Matrix4x4 viewProj, ref bool atlasBound)
    {
        if (state.IsEmpty || state.Profile is not { } profile) return;
        var light = owner.LightAt(cell + new Vector3(0.5f, 1.2f, 0.5f));
        var surface = cell + new Vector3(0.5f, profile.LiquidY, 0.5f);
        if (state.Water)
        {
            stations.BindAtlas(ref atlasBound);
            var liquid = Matrix4x4.CreateScale(VatInnerWidth) * Matrix4x4.CreateTranslation(surface);
            owner.SetDrawUniforms(liquid, liquid * viewProj, 1f, light);
            stations.VatLiquid(TanningRules.CanTan(state) || state.Progress > 0f).Render();
        }
        if (state.Hides > 0)
            DrawPile(profile.InputId, state.Hides, 1f, surface, 0f, WorkPieceSize, light, viewProj, ref atlasBound);
        if (state.Leather > 0)
            DrawPile(profile.ResultId, state.Leather, 1f, cell + new Vector3(0.5f, VatFloorY, 0.5f), 0f, WorkPieceSize,
                light, viewProj, ref atlasBound);
    }

    private void DrawPile(ushort id, int count, float topScale, Vector3 foot, float yaw, float pieceSize,
        EntityLightSample light, Matrix4x4 viewProj, ref bool atlasBound)
    {
        if (count <= 0 || id == 0 || stations.BindStationMesh(id, ref atlasBound) is not { } mesh) return;

        var isItem = ItemRegistry.IsItem(id);
        var rise = 0f;
        for (var i = 0; i < count; i++)
        {
            var size = pieceSize * (i == count - 1 ? topScale : 1f);
            if (size <= 0f) break;
            var twist = yaw + (i % 2 == 0 ? 1f : -1f) * StackTwist * (i % 3);
            var placed = isItem
                ? Matrix4x4.CreateScale(size)
                  * SpritePose.LyingFaceUp(twist)
                  * Matrix4x4.CreateTranslation(foot + new Vector3(0f, rise + SpritePose.RestingLift(size), 0f))
                : Matrix4x4.CreateScale(size)
                  * Matrix4x4.CreateRotationY(twist)
                  * Matrix4x4.CreateTranslation(foot + new Vector3(0f, rise + size * 0.5f, 0f));
            owner.SetDrawUniforms(placed, placed * viewProj, 1f, light);
            mesh.Render();
            rise += isItem
                ? SpritePose.HalfThickness(size, SpritePose.ItemSpriteFrame) * 2f + SpritePose.RestingClearance
                : size;
        }
    }
}
