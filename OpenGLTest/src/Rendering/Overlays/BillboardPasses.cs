using System.Collections.Generic;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Graphics;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The billboard draws of the entity pass: the five-heart healthbars over the mobs, the floating
/// damage numbers, the goblins' notice pips and the falling-leaf, fire and bubble particles. Holds
/// no GL resource and issues no GL call of its own: the six renderers stay owned by
/// <see cref="RenderManager"/> (their creation is not one contiguous run) and, like the frame's
/// light and the texture units, are read live through it. Render thread only.
/// </summary>
internal sealed class BillboardPasses
{
    private readonly RenderManager _owner;

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;

    internal BillboardPasses(RenderManager owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Draws the five-heart healthbar above every live mob (see
    /// <see cref="EntityHealthbarRenderer"/>), right after the bodies so the row tracks them.
    /// </summary>
    internal void RenderEntityHealthbars(Camera camera, IReadOnlyList<Mob> mobs)
    {
        if (camera == null || mobs.Count == 0 || _owner.Healthbars == null) return;

        _owner.Healthbars.Render(mobs, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            Lighting.FogColor, Lighting.FogNear, Lighting.FogFar);
        // The healthbar renderer binds its own heart sprites to unit 0.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Draws the floating damage numbers over struck mobs (see <see cref="DamageNumberRenderer"/>),
    /// right after the healthbars they rise from and with the same depth and fog rules.
    /// </summary>
    internal void RenderDamageNumbers(Camera camera, DamageNumberPool numbers)
    {
        if (camera == null || numbers.Count == 0 || _owner.DamageNumbers == null) return;

        _owner.DamageNumbers.Render(numbers, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            Lighting.FogColor, Lighting.FogNear, Lighting.FogFar);
        // The number renderer binds the font atlas to unit 0.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Draws the "!" over every goblin that just noticed a player (see <see cref="NoticePipRenderer"/>),
    /// with the damage numbers' depth and fog rules.
    /// </summary>
    internal void RenderNoticePips(Camera camera, IReadOnlyList<Goblin> goblins)
    {
        if (camera == null || goblins.Count == 0 || _owner.NoticePips == null) return;

        _owner.NoticePips.Render(goblins, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            Lighting.FogColor, Lighting.FogNear, Lighting.FogFar);
        // The pip renderer binds the font atlas to unit 0.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Draws every live falling leaf in one batched billboard draw. Leaves are too small to
    /// cast shadows, so they skip the shadow pass; their own shader carries the frame light
    /// and fog, which is final by the time the entity pass runs.
    /// </summary>
    internal void RenderLeafParticles(Camera camera, IReadOnlyList<LeafParticle> leaves)
    {
        if (camera == null || leaves.Count == 0 || _owner.Leaves == null) return;

        _owner.Leaves.Render(leaves, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            _owner.AtlasTexture, TextureAtlas.TileWidth, Lighting.SunDir, Lighting.SunColor, Lighting.SunIntensity,
            Lighting.Ambient, Lighting.FogColor, Lighting.FogNear, Lighting.FogFar);
        // LeafParticleRenderer binds unit 0 itself. It happens to be the same atlas today, but
        // the renderer does not promise that.
        Units.InvalidateTextureUnits();
    }

    internal void RenderFireParticles(Camera camera, IReadOnlyList<FireParticle> fire)
    {
        if (camera == null || fire.Count == 0 || _owner.Fire == null) return;

        _owner.Fire.Render(fire, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            Lighting.FogColor, Lighting.FogNear, Lighting.FogFar);
    }

    internal void RenderBubbleParticles(Camera camera, IReadOnlyList<BubbleParticle> bubbles)
    {
        if (camera == null || bubbles.Count == 0 || _owner.Bubbles == null) return;

        _owner.Bubbles.Render(bubbles, camera.GetViewProjection(), camera.RenderEye, camera.ViewDirection,
            Lighting.FogColor, Lighting.FogNear, Lighting.FogFar);
        // BubbleParticleRenderer binds its own sprite to unit 0.
        Units.InvalidateTextureUnits();
    }
}
