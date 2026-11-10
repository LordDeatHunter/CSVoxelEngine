using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using VoxelEngine.Content;
using VoxelEngine.Data;
using VoxelEngine.Juice;
using VoxelEngine.Player;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.Chest;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.SpellBuilder;
using VoxelEngine.UI.Forge;
using VoxelEngine.UI.Waystone;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The UI hand-off: the HUD pass over the finished frame (the underwater wash, the damage
/// vignette, the crosshair and look-at label, the compass or clock, the hotbar, hearts and toasts,
/// the [E] panel, the spell builder, the waystone screen and the pause menu), the chat, tooltip and player-list passes after it, and the
/// world-less frames: the main menu, the opaque loading screen and the loading fade over the first
/// playing frames. Every pass draws through the one <see cref="UIRenderer"/> its owner builds,
/// straight into the default framebuffer in display space. Render thread only.
/// </summary>
internal sealed class UiPasses
{
    private readonly GL _gl;
    private readonly RenderManager _owner;

    private FrameLighting Lighting => _owner.Lighting;
    private TextureUnits Units => _owner.Units;
    private RenderTargets Targets => _owner.Targets;

    // Space between the boss bar block and the look-at label pushed below it
    private const float BossBarLabelGap = 8f;

    internal UiPasses(GL gl, RenderManager owner)
    {
        _gl = gl;
        _owner = owner;
    }

    internal void RenderUI(IWindow window, Inventory inventory, InventoryView view, float time,
        int fps, bool showProfiler, Camera? camera = null, bool showCoordinates = true,
        PauseMenuView? menu = null, SettingsMenuView? settings = null,
        int health = PlayerHealth.MaxHealth, float airShare = 1f, float manaShare = 1f, float damageFlash = 0f, HotbarNameFade? hotbarName = null,
        HotbarPickupPop? hotbarPops = null, double lightingTime = 0.0, HudToast? toast = null,
        CrosshairState crosshair = default, LookLabelState? lookAt = null, string? staffSlot = null,
        byte effectMask = 0, SpellBuilderView? builder = null, int maxHealth = PlayerHealth.MaxHealth,
        ChestView? chest = null, WaystoneView? waystone = null, BossBarState? boss = null, ForgeView? forge = null)
    {
        // Any panel (the [E] inventory, the spell builder, the waystone screen or the anvil's recipe list) hides the
        // crosshair and the status row.
        var panelUp = view.IsOpen || builder?.IsOpen == true || waystone?.IsOpen == true || forge?.IsOpen == true;
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize, UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));

        // Cheap underwater wash over the whole view: the world fog alone leaves nearby
        // geometry untinted, and this is what sells "the camera is in the water"
        if (Lighting.UnderwaterT > 0.001f)
        {
            var wet = MathUtils.SmoothStep(Lighting.UnderwaterT);
            var tint = Lighting.EyeInLava ? FrameLighting.LavaFogColor : FrameLighting.UnderwaterFogColor;
            var alpha = (Lighting.EyeInLava ? 0.35f : 0.18f) * wet;
            _owner.Ui.DrawRect(_owner.Ui.ScreenCenter, _owner.Ui.ScreenSize,
                Rgba.FromFloat(tint.X, tint.Y, tint.Z, alpha));
        }

        // The hit flash and the low-health pulse share the vignette's one full-screen pass
        var lowHealth = LowHealthVignette.Intensity((float)health / maxHealth, time,
            dead: health <= 0, creative: camera?.IsCreative == true);
        // ...and so do the damage direction arc toward the last blow (DamageDirection) and the
        // droplets streaking down after the eye surfaced (SurfacingDroplets)
        var hitArc = camera?.DamageArcStrength ?? 0f;
        var droplets = camera?.Droplets.Intensity ?? 0f;
        if (damageFlash > 0.001f || lowHealth > 0.001f || hitArc > 0.001f || droplets > 0.001f)
            _owner.Vignette?.Render(window.Size, damageFlash, lowHealth, time, hitArc,
                camera?.DamageArcScreenDirection ?? Vector2.Zero,
                droplets, camera?.Droplets.Seconds ?? 0f, camera?.Droplets.Seed ?? 0);

        // The compass and the clock share the top-center strip; the look-at label rides below it
        var hasCompass = camera != null && HasCompassEquipped(inventory);
        var hasClock = camera != null && HasClockEquipped(inventory);
        if (!panelUp && (menu == null || !menu.IsOpen))
        {
            _owner.Ui.Hud.DrawCrosshair(crosshair.ThrowCharge, crosshair.BreakProgress, crosshair.HitMarker);
            // The boss bar takes the spot under the strip, pushing the look-at label below it
            var labelTop = TopStripRenderer.LookLabelTop(hasCompass, hasClock, showCoordinates);
            if (boss is { } bar)
            {
                _owner.Ui.Hud.DrawBossBar(bar.Name, bar.Share, labelTop);
                labelTop += HudRenderer.BossBarBlockHeight + BossBarLabelGap;
            }
            // The look-at label (its debug lines ride the profiler overlay, i.e. X)
            if (lookAt is { } label)
                _owner.Ui.TopStrip.DrawLookLabel(label, time, showProfiler, labelTop);
        }
        if (hasCompass)
            _owner.Ui.TopStrip.DrawCompass(camera!.Yaw, camera.Position, showCoordinates);
        else if (hasClock)
            _owner.Ui.TopStrip.DrawClock(Clock.DayNumber(lightingTime), Clock.Readout(lightingTime));
        _owner.Ui.Hud.BeginBars(time);
        _owner.Ui.Hud.DrawFps(fps);
        if (showProfiler)
            _owner.Ui.Hud.DrawProfiler(FrameProfiler.Lines);
        var heading = camera != null ? Compass.HeadingFromYaw(camera.Yaw) : 0f;
        _owner.Ui.Hud.DrawHotbar(inventory, heading, time, hotbarPops);
        if (hotbarPops != null && camera != null)
            _owner.Ui.Hud.DrawFlyingPickups(hotbarPops.Flying, camera.GetViewMatrix() * camera.GetProjectionMatrix(),
                heading, time);
        // The day/first-pickup/full-inventory toast rides above the item-name row
        if (toast is { Alpha: > 0f, Text.Length: > 0 })
            _owner.Ui.Hud.DrawToast(toast.Text, toast.Alpha);
        if (!panelUp && (menu == null || !menu.IsOpen))
        {
            _owner.Ui.Hud.DrawHearts(health, maxHealth);
            _owner.Ui.Hud.DrawAir(airShare, health);
            _owner.Ui.Hud.DrawMana(manaShare, health, camera?.IsCreative == true);
            _owner.Ui.Hud.DrawEffects(effectMask, health);
            if (staffSlot != null)
                _owner.Ui.Hud.DrawStaffSlot(staffSlot);
            if (hotbarName != null)
                _owner.Ui.Hud.DrawHotbarName(hotbarName.Name, hotbarName.Alpha);
        }
        view.Transition.Step(view.IsOpen, time);
        view.StepMotion(time);
        if (view.IsOpen || view.Transition.Visible)
        {
            _owner.Ui.SetOverlay(new Vector2(0f, view.Transition.SlideY), view.Transition.Alpha);
            _owner.Ui.Panel.DrawInventoryPanel(inventory, view, time, heading, camera?.IsCreative == true,
                chest is { IsOpen: true });
            _owner.Ui.ClearOverlay();
        }
        if (chest != null)
        {
            chest.Transition.Step(chest.IsOpen, time);
            if (chest.IsOpen || chest.Transition.Visible)
            {
                _owner.Ui.SetOverlay(new Vector2(0f, chest.Transition.SlideY), chest.Transition.Alpha);
                _owner.Ui.ChestCard.Draw(chest, view.Focus == InventoryFocus.ChestSearch, view.HoverCanvas, time, heading);
                _owner.Ui.ClearOverlay();
            }
        }
        // A row dragged toward a slot rides the pointer over the panel, the chest card and the hotbar
        if (view.IsOpen && view.Drag is { Lifted: true } drag && view.HoverCanvas is { } pointer)
            _owner.Ui.Panel.DrawDraggedRow(drag.Id, pointer, heading, time);
        if (builder != null)
        {
            builder.Transition.Step(builder.IsOpen, time);
            if (builder.IsOpen || builder.Transition.Visible)
            {
                _owner.Ui.SetOverlay(new Vector2(0f, builder.Transition.SlideY), builder.Transition.Alpha);
                _owner.Ui.SpellBuilder.DrawSpellBuilder(inventory, builder);
                _owner.Ui.ClearOverlay();
            }
        }
        if (waystone != null)
        {
            waystone.Transition.Step(waystone.IsOpen, time);
            if (waystone.IsOpen)
            {
                _owner.Ui.SetOverlay(new Vector2(0f, waystone.Transition.SlideY), waystone.Transition.Alpha);
                _owner.Ui.Waystone.Draw(waystone, time);
                _owner.Ui.ClearOverlay();
            }
        }
        if (forge != null)
        {
            forge.Transition.Step(forge.IsOpen, time);
            if (forge.IsOpen)
            {
                _owner.Ui.SetOverlay(new Vector2(0f, forge.Transition.SlideY), forge.Transition.Alpha);
                _owner.Ui.Forge.Draw(forge, time, camera != null ? Compass.HeadingFromYaw(camera.Yaw) : 0f);
                _owner.Ui.ClearOverlay();
            }
        }
        if (menu != null && settings != null)
            _owner.Ui.Menu.DrawPauseMenu(menu, settings);
        _owner.Ui.End();
        // The font and the item icons take unit 0, and this pass sits across the frame boundary.
        Units.InvalidateTextureUnits();
    }

    /// <summary>Draws the chat HUD (the log and, while open, the input line) in its own UI pass over the world.</summary>
    internal void RenderChat(IWindow window, ChatView chat, double time)
    {
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize,
            UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));
        _owner.Ui.Chat.DrawChat(chat, (float)time);
        _owner.Ui.End();
        Units.InvalidateTextureUnits();
    }

    /// <summary>Draws the death screen (see <see cref="MenuRenderer.DrawDeathScreen"/>) in its own UI pass over the world and the HUD.</summary>
    internal void RenderDeathScreen(IWindow window, string? deathLine)
    {
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize,
            UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));
        _owner.Ui.Menu.DrawDeathScreen(deathLine);
        _owner.Ui.End();
        Units.InvalidateTextureUnits();
    }

    /// <summary>Draws one hover tooltip (see <see cref="HudRenderer.DrawTooltip"/>), or nothing when
    /// the cursor hovers nothing. <paramref name="time"/> is the world's lighting clock, the one the
    /// rows' icons and chrome animate on.</summary>
    internal void RenderTooltip(IWindow window, HoverTooltip? tooltip, float time)
    {
        if (tooltip is not { Lines.Count: > 0 } hover) return;
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize,
            UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));
        // The anchor is the pointer in window pixels; the box is laid out and drawn in canvas units
        _owner.Ui.Hud.DrawTooltip(_owner.Ui.ToCanvas(hover.Anchor), hover.Lines, hover.Colors, hover.Rarity, time);
        _owner.Ui.End();
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// Draws the Tab player list (see <see cref="HudRenderer.DrawPlayerList"/>) over the HUD while
    /// the key is held: the local player first, then every other player on the server.
    /// </summary>
    internal void RenderPlayerList(IWindow window, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize,
            UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));
        _owner.Ui.Hud.DrawPlayerList(names);
        _owner.Ui.End();
        Units.InvalidateTextureUnits();
    }

    // Clear color of a menu frame; the menu and loading backdrops cover it anyway.
    private static readonly Vector4 MenuClearColor = new(0.06f, 0.07f, 0.09f, 1f);

    /// <summary>
    /// A whole frame with no world: the main menu (<paramref name="menu"/>) or, when
    /// <paramref name="loading"/> is set, the opaque loading / saving screen. Nothing HDR runs -
    /// no <c>RenderManager.BeginFrame</c>, <c>FrameLighting.SetLighting</c>,
    /// <c>TerrainPasses.CullWater</c> or <c>ScreenPasses.RenderPostProcess</c>, and no GPU timer
    /// bracket - and <see cref="RenderManager.LightSource"/> may be null. The default
    /// framebuffer is cleared and written directly, in display space like the HUD. Render thread only.
    /// </summary>
    internal void RenderMenuFrame(IWindow window, MainMenuView? menu, SettingsMenuView settings,
        LoadingScreenView? loading, double time, Vector2? pointerWindow)
    {
        Units.InvalidateTextureUnits();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(Targets.ViewportSize);
        _gl.ClearColor(MenuClearColor.X, MenuClearColor.Y, MenuClearColor.Z, MenuClearColor.W);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize,
            UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));
        // Menu buttons light up from the pointer, which the gameplay pass sets but this frame never reaches.
        _owner.Ui.Pointer = pointerWindow is { } p ? _owner.Ui.ToCanvas(p) : null;
        if (loading != null)
            _owner.Ui.LoadingScreen.DrawLoadingScreen(loading, time, opaque: true);
        else if (menu != null)
            _owner.Ui.Menu.DrawMainMenu(menu, settings, (float)time);
        _owner.Ui.End();
        // The font takes unit 0, and this pass sits across the frame boundary.
        Units.InvalidateTextureUnits();
    }

    /// <summary>
    /// The loading screen fading out over the first playing frames, drawn translucently over
    /// whatever the frame already holds. Program calls it after <c>WorldRenderer.RenderUI</c>, where
    /// framebuffer 0 is already bound. Render thread only.
    /// </summary>
    internal void RenderLoadingOverlay(IWindow window, LoadingScreenView loading, double time)
    {
        var windowSize = new Vector2(window.Size.X, window.Size.Y);
        _owner.Ui.Begin(window.Size, window.FramebufferSize,
            UiCanvas.EffectiveScale(GameSettings.RequestedGuiScale, windowSize, UiCanvas.ReferenceSize));
        _owner.Ui.LoadingScreen.DrawLoadingScreen(loading, time, opaque: false);
        _owner.Ui.End();
        Units.InvalidateTextureUnits();
    }

    // The compass item id, resolved once: null when no item named "compass" is registered,
    // in which case the HUD compass never shows rather than throwing every frame.
    private ushort? _compassId;
    private bool _compassResolved;

    private bool HasCompassEquipped(Inventory inventory)
    {
        if (!_compassResolved)
        {
            _compassResolved = true;
            try
            {
                _compassId = ContentRegistry.RequireItem("compass").Id;
            }
            catch (InvalidOperationException)
            {
                _compassId = null;
            }
        }
        return _compassId.HasValue && inventory.ContainsInHotbar(_compassId.Value);
    }

    // The clock item id, resolved once like the compass's
    private ushort? _clockId;
    private bool _clockResolved;

    private bool HasClockEquipped(Inventory inventory)
    {
        if (!_clockResolved)
        {
            _clockResolved = true;
            try
            {
                _clockId = ContentRegistry.RequireItem("clock").Id;
            }
            catch (InvalidOperationException)
            {
                _clockId = null;
            }
        }
        return _clockId.HasValue && inventory.ContainsInHotbar(_clockId.Value);
    }
}
