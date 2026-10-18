using Silk.NET.OpenGL;
using VoxelEngine.Graphics;

namespace VoxelEngine.Rendering;

/// <summary>
/// The one texture-unit binder every render pass binds through (PERF_TODO R5): the single
/// <see cref="TextureUnitCache"/> of what units 0-4 hold and which unit is selected, and the
/// primitives that skip a redundant select or bind against it. Render thread only.
/// </summary>
internal sealed class TextureUnits
{
    private readonly GL _gl;

    // PERF_TODO R5. What the context already has selected and bound on units 0-4. Every
    // render-path ActiveTexture/BindTexture in the renderer goes through the three primitives
    // below, so a redundant one costs nothing; everything that can change those units behind
    // their back calls InvalidateTextureUnits (see its doc comment for the full audit).
    // Render thread only, like every other GL call here (AGENTS.md) - plain field, no lock.
    private readonly TextureUnitCache _texUnits = new();

    internal TextureUnits(GL gl)
    {
        _gl = gl;
    }

    /// <summary>Selects a texture unit. Issued wherever the renderer used to issue a bare
    /// <c>gl.ActiveTexture</c>, 1:1.</summary>
    internal void ActiveUnit(int unit)
    {
        if (_texUnits.MarkActive(unit)) _gl.ActiveTexture(TextureUnit.Texture0 + unit);
    }

    /// <summary>
    /// Replaces a <c>gl.ActiveTexture</c> + <c>gl.BindTexture</c> pair, 1:1. The select is
    /// unconditional work for the cache (it may still skip the GL call), so the active unit
    /// after this call is exactly what it is without the cache - which matters because
    /// <see cref="RenderTargets.CopyScene"/>'s <c>glCopyTexSubImage2D</c>, the three <c>Create*Target</c>
    /// methods (see the remark on <see cref="RenderTargets.CreateRaysTarget"/>) and
    /// <see cref="TextureLoader.Upload"/> all act on whatever unit happens to be active. Only
    /// the redundant bind is elided here, never the trajectory.
    /// </summary>
    internal void BindUnit(int unit, TextureTarget target, uint texture)
    {
        ActiveUnit(unit);
        if (_texUnits.MarkBound(unit, (int)target, texture)) _gl.BindTexture(target, texture);
    }

    /// <summary>
    /// <see cref="RenderManager.BindWorldTextures"/> ONLY. Skips the select as well when the binding already
    /// matches, which is what turns a fully redundant replay into zero GL calls. Safe by a
    /// purely local argument: nothing between BindWorldTextures' five binds reads the active
    /// unit, and the method ends with an unconditional <c>ActiveUnit(0)</c> that restores its
    /// documented postcondition. Do not use it anywhere else without repeating that argument -
    /// in particular not near a copy, an upload, or a call out to another class.
    /// </summary>
    internal void BindLazy(int unit, TextureTarget target, uint texture)
    {
        if (_texUnits.Matches(unit, (int)target, texture)) return;
        ActiveUnit(unit);
        _gl.BindTexture(target, texture);
        _texUnits.MarkBound(unit, (int)target, texture);
    }

    /// <summary>
    /// Forgets the tracked unit state, so the next <see cref="BindUnit"/> / <see cref="BindLazy"/>
    /// re-issues its GL calls. This is the maintained audit of every site that can change unit 0-4
    /// or the active unit behind the primitives' back; each one calls this.
    /// <para><b>Invalidates:</b>
    /// <see cref="RenderManager.BeginFrame"/> (the once-a-frame hard reset);
    /// <see cref="RenderManager.LoadResources"/> (the sun upload, <c>TextureAtlas.Build</c>,
    /// <c>TextureLoader.LoadFromFile</c> and the target creators all bind raw);
    /// <see cref="RenderManager.CreateShadowMap"/> (both cascades; the far map's unit 7 is untracked
    /// anyway);
    /// the cloud-shadow map's one-time unit-8 bind in <see cref="RenderManager.LoadResources"/>
    /// (untracked, and covered by LoadResources' own closing call, since it leaves unit 0 active and
    /// changes the unit-0 binding through the constructor's raw bind);
    /// <see cref="ReflectionPass.CreateReflectionTarget"/> on its recreate path only (it is called
    /// every frame and early-returns without binding when the size is unchanged) and
    /// <see cref="ReflectionPass.DeleteReflectionTarget"/>;
    /// <see cref="RenderTargets.CreateSceneCopy"/> / <see cref="RenderTargets.DeleteSceneCopy"/>;
    /// <see cref="RenderTargets.CreateRaysTarget"/> / <see cref="RenderTargets.DeleteRaysTarget"/>;
    /// <see cref="RenderTargets.CreateHdrTarget"/> / <see cref="RenderTargets.DeleteHdrTarget"/>;
    /// <see cref="RenderManager.SetViewport"/>;
    /// <see cref="Overlays.BillboardPasses.RenderLeafParticles"/> and
    /// <see cref="Overlays.BillboardPasses.RenderBubbleParticles"/> (both renderers bind their own
    /// sprite to unit 0);
    /// <see cref="ScreenSpace.ScreenPasses.RenderUnderwaterRays"/> (the rays renderer detours
    /// through unit 5);
    /// <see cref="Overlays.UiPasses.RenderUI"/> (the font and the item icons take unit 0, and this
    /// pass spans the frame boundary);
    /// and <c>ItemMeshCache.BuildTexture</c> through its <c>GlStateChanged</c> callback - item
    /// textures are built lazily <em>inside</em> the entity pass and <c>TextureLoader.Upload</c>
    /// binds on whatever unit is live. Every <c>glDeleteTextures</c> is in this list too, because
    /// deleting a texture reverts its bindings to 0 in the current context; the only ones that are
    /// not are <see cref="RenderManager.Dispose"/>'s and the other classes' Dispose-time deletes,
    /// which run after the last frame.
    /// </para>
    /// <para><b>Provably cannot invalidate</b>, so do not add a call "just in case":
    /// <c>CloudRenderer</c>, <c>FireParticleRenderer</c> and the selection pass contain no
    /// <c>ActiveTexture</c>/<c>BindTexture</c> at all - their VAO/VBO binds are buffer
    /// state, and the program they switch is handled by <c>Shader.Use</c>'s own cache.
    /// <see cref="Overlays.SelectionPasses.RenderBlockBreaking"/> binds its crack sprite through
    /// <see cref="BindUnit"/>, which updates the cache rather than invalidating it, so it needs no
    /// call either; so does <see cref="ScreenSpace.ScreenPasses.RenderPostProcess"/> with the HDR
    /// color texture on unit 0 (and its unbind).
    /// <c>ChunkDrawBatch</c>, <c>ChunkGeometryPool</c> and <c>Mesh.Render</c> touch VAO/VBO/indirect
    /// state only. <see cref="RenderTargets.CopyScene"/>'s <c>glCopyTexSubImage2D</c> changes texture
    /// <em>contents</em>, not bindings (its own select/bind pair is routed through
    /// <see cref="BindUnit"/> precisely because the copy destination is the active unit's binding).
    /// <c>BitmapFont</c>'s texture creation runs in the <c>UIRenderer</c> constructor, before the
    /// first frame. <see cref="FrameLighting.SetLighting"/> runs between the UI pass and
    /// <see cref="RenderManager.BeginFrame"/> and issues no texture bind, which is what makes the top
    /// of BeginFrame a valid reset point. <see cref="Atmosphere.SkyPasses.RenderSun"/> early-returns
    /// on invisibility <em>before</em> its unit-0 bind, so on a night frame it leaves unit 0 alone -
    /// BeginFrame's reset covers both cases.
    /// </para>
    /// </summary>
    internal void InvalidateTextureUnits() => _texUnits.Invalidate();
}
