using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Player;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using static VoxelEngine.Rendering.Overlays.UIRenderer;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The HUD over the world: the crosshair, the hearts, the air, the mana bar, the status-effect icons, the hotbar and the selected item's name, the
/// held staff's spell slot, the
/// toast line, the FPS counter and the profiler table, the hover tooltip and the Tab player list,
/// plus the hotbar hit-test over the same <see cref="HotbarLayout"/> the draw reads. It owns the
/// crosshair, heart and effect-icon sprites, which <see cref="UIRenderer"/> creates and deletes through it at
/// their original points, and draws through the <see cref="UIRenderer"/> primitives. Render thread
/// only.
/// </summary>
internal sealed class HudRenderer : IDisposable
{
    private const float CrosshairSize = 32f;

    // Hearts row above the hotbar, in pixels. The sprites are 32px wide with 1px of empty
    // padding down each side, so only the middle 30px are drawn and the stride matches it:
    // neighbors sit exactly edge to edge with no double-padding gap.
    private const float HeartDrawWidth = 30f;
    private const float HeartDrawHeight = 32f;
    private const float HeartStride = 30f;
    private const float HeartGap = 10f;
    // The air bubbles ride a row above the hearts, right-aligned with it like Minecraft's
    private const float BubbleDrawSize = 34f;
    private const float BubbleStride = 28f;
    private const float BubbleGap = 6f;
    // The mana bar sits in the margin under the hotbar, so it never meets the bubbles, the item name
    // or the toast, which all stack above the hearts
    private const float ManaBarHeight = 6f;
    private static readonly Rgba ManaTrack = Rgba.Gray(0.04f, 0.6f);
    private static readonly Rgba ManaFill = new(70, 130, 255, 230);
    // The selected item's name (DrawHotbarName) sits this far above the hearts row, leaving room
    // for descenders.
    private const float HotbarNameGap = 12f;
    // The held staff's slot line (DrawStaffSlot) starts this far right of the hotbar row's right edge
    private const float StaffSlotGap = 12f;

    // Hotbar metrics, in pixels
    // Aliases of the shared hotbar geometry (HotbarLayout), so the HUD draws read short names.
    private const float SlotSize = HotbarLayout.SlotSize;
    private const float SlotGap = HotbarLayout.SlotGap;
    private const float BottomMargin = HotbarLayout.BottomMargin;
    // The toast row sits this far above the item-name row, so the two never overlap
    private const float ToastRowGap = 26f;
    // Smallest width of the Tab player list panel; a long name widens it
    private const float PlayerListWidth = 200f;

    // Fraction of a slot the icon's footprint fills, leaving a margin inside the border
    private const float IconFill = 0.72f;

    private const float CountPadRight = 5f;
    private const float CountPadBottom = 4f;

    // The FPS counter's top-left corner, in pixels; the profiler table starts one line below it
    private const float FpsTop = 10f;

    // Frame-timing overlay (DrawProfiler), in pixels
    private const float ProfilerLeft = 10f;
    private const float ProfilerPadding = 6f;
    private static readonly Rgba ProfilerBackground = Rgba.Gray(0.04f, 0.6f);

    private readonly GL _gl;
    private readonly UIRenderer _ui;
    private readonly uint _crosshairTexture;
    private readonly uint _heartTexture;
    private readonly uint _heartEmptyTexture;
    private readonly uint _bubbleTexture;
    private readonly Mesh _bubbleMesh;
    // One HUD icon per status effect id (StatusEffect.Id), 0 where the effect names no icon
    private readonly uint[] _effectIcons = new uint[StatusEffect.MaxKinds];

    // Heart sprites with the 1px side padding cropped out of their UVs (see the Heart
    // metrics above): full width for empty/full, left half for a half heart.
    private readonly Mesh _heartEmptyMesh;
    private readonly Mesh _heartFullMesh;
    private readonly Mesh _heartHalfMesh;

    // The crosshair's five cropped pieces (see DrawCrosshair)
    private readonly Mesh _crosshairCenterMesh;
    private readonly Mesh[] _crosshairArmMeshes;

    internal HudRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;

        _crosshairTexture = TextureLoader.LoadFromFile(gl, "assets/textures/crosshair.png");
        _heartTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/heart.png");
        _heartEmptyTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/heart_empty.png");
        _bubbleTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/bubble.png");
        _bubbleMesh = Primitives.CreateRectangle(gl, 1f, 1f);
        foreach (var effect in StatusEffectRegistry.All)
            if (effect.IconPath != null)
                _effectIcons[effect.Id] = TextureLoader.LoadFromFile(gl, effect.IconPath);
        var heartUv = new UvRect(new Vector2(1f / 32f, 0f), new Vector2(31f / 32f, 1f));
        var heartHalfUv = new UvRect(new Vector2(1f / 32f, 0f), new Vector2(16f / 32f, 1f));
        _heartEmptyMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: heartUv);
        _heartFullMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: heartUv);
        _heartHalfMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: heartHalfUv);

        // The crosshair's five pieces, cropped out of the 16x16 texture: a 4x4 center dot and four
        // 5x4 pills with an empty row and column between them (see DrawCrosshair)
        _crosshairCenterMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: CrosshairUv(6f, 6f, 10f, 10f));
        _crosshairArmMeshes =
        [
            Primitives.CreateRectangle(gl, 1f, 1f, uv: CrosshairUv(6f, 0f, 10f, 5f)),
            Primitives.CreateRectangle(gl, 1f, 1f, uv: CrosshairUv(6f, 11f, 10f, 16f)),
            Primitives.CreateRectangle(gl, 1f, 1f, uv: CrosshairUv(0f, 6f, 5f, 10f)),
            Primitives.CreateRectangle(gl, 1f, 1f, uv: CrosshairUv(11f, 6f, 16f, 10f)),
        ];
    }

    // One crosshair piece's pixel rect (y measured from the texture's top) as a UV rect
    private static UvRect CrosshairUv(float x0, float y0, float x1, float y1) =>
        new(new Vector2(x0 / CrosshairTextureSize, 1f - y1 / CrosshairTextureSize),
            new Vector2(x1 / CrosshairTextureSize, 1f - y0 / CrosshairTextureSize));

    /// <summary>
    /// The crosshair, cropped out of the 16x16 texture into its five pieces: the center dot stays
    /// put while the four arms slide outward with a throw's charge (snapping back on release), a
    /// ring of segments around them fills with <paramref name="breakProgress"/> while a block is
    /// mined (nothing at zero, so creative never shows it) and an X flashes at
    /// <paramref name="hitMarker"/> when a hit lands.
    /// </summary>
    internal void DrawCrosshair(float throwCharge = 0f, float breakProgress = 0f, float hitMarker = 0f)
    {
        var center = _ui.ScreenCenter;
        var unit = CrosshairSize / CrosshairTextureSize;
        var spread = MathUtils.SmoothStep(MathUtils.Clamp01(throwCharge)) * CrosshairSpreadPx;

        // The pieces' pixel rects in the texture: the dot at the middle, one pill per direction,
        // an empty row and column between each
        _ui.DrawMesh(_crosshairCenterMesh, RectTransform(center, new Vector2(4f * unit)), Rgba.White, _crosshairTexture);
        for (var i = 0; i < _crosshairArmMeshes.Length; i++)
        {
            var (size, offset) = i switch
            {
                0 => (new Vector2(4f, 5f), new Vector2(0f, -5.5f)),
                1 => (new Vector2(4f, 5f), new Vector2(0f, 5.5f)),
                2 => (new Vector2(5f, 4f), new Vector2(-5.5f, 0f)),
                _ => (new Vector2(5f, 4f), new Vector2(5.5f, 0f)),
            };
            var position = center + new Vector2(offset.X * unit, offset.Y * unit)
                + (i switch
                {
                    0 => new Vector2(0f, -spread),
                    1 => new Vector2(0f, spread),
                    2 => new Vector2(-spread, 0f),
                    _ => new Vector2(spread, 0f),
                });
            _ui.DrawMesh(_crosshairArmMeshes[i], RectTransform(position, size * unit), Rgba.White, _crosshairTexture);
        }

        // The mining ring: segments around the crosshair, the lit share of them following the
        // block's break progress
        var progress = MathUtils.Clamp01(breakProgress);
        if (progress > 0.001f)
        {
            var lit = (int)MathF.Ceiling(progress * CrosshairRingSegments);
            for (var i = 0; i < CrosshairRingSegments; i++)
            {
                var angle = i / (float)CrosshairRingSegments * MathF.Tau - MathF.PI * 0.5f;
                var position = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * CrosshairRingRadius;
                _ui.DrawRect(position, new Vector2(CrosshairRingDot), i < lit ? Rgba.White : Rgba.Gray(1f, 0.22f));
            }
        }

        // The hit marker: two bars crossed at 45 degrees, fading out with the mark
        var mark = MathUtils.Clamp01(hitMarker);
        if (mark > 0.001f)
        {
            var color = new Rgba(255, 96, 96, (byte)(mark * 255f + 0.5f));
            for (var i = 0; i < 2; i++)
            {
                var rotation = Matrix4x4.CreateRotationZ((i == 0 ? 1f : -1f) * MathF.PI * 0.25f);
                var model = Matrix4x4.CreateScale(CrosshairMarkerLength, CrosshairMarkerWidth, 1f)
                            * rotation
                            * Matrix4x4.CreateTranslation(center.X, center.Y, 0f);
                _ui.DrawMesh(_ui.UnitSquare, model, color);
            }
        }
    }

    // The crosshair texture's edge in pixels (its pieces' rects are authored against it)
    private const float CrosshairTextureSize = 16f;
    // How far a fully charged throw pushes the arms outward, in canvas units
    private const float CrosshairSpreadPx = 7f;
    // The mining ring: segments around the crosshair and the ring's radius and dot size
    private const int CrosshairRingSegments = 12;
    private const float CrosshairRingRadius = 18f;
    private const float CrosshairRingDot = 3.5f;
    // The hit marker's bars: their length and thickness
    private const float CrosshairMarkerLength = 26f;
    private const float CrosshairMarkerWidth = 3f;

    /// <summary>
    /// The survival hearts: 10 in a row centered above the hotbar, 1 heart per 10 HP.
    /// Every heart draws its empty shell first and the red heart over it — full, left
    /// half (5 HP steps), or nothing — so damage reads heart by heart like Minecraft.
    /// </summary>
    internal void DrawHearts(int health, int maxHealth = PlayerHealth.MaxHealth)
    {
        var hp = Math.Clamp(health, 0, maxHealth);
        // Ten hearts always: each stands for a tenth of the pool, so gear that grows the pool grows every heart
        var perHeart = maxHealth / (float)PlayerHealth.HeartCount;
        var rowWidth = HeartDrawWidth + (PlayerHealth.HeartCount - 1) * HeartStride;
        var firstX = (_ui.ScreenSize.X - rowWidth) * 0.5f + HeartDrawWidth * 0.5f;
        var hotbarTop = _ui.ScreenSize.Y - BottomMargin - SlotSize;
        var centerY = hotbarTop - HeartGap - HeartDrawHeight * 0.5f;
        var fullSize = new Vector2(HeartDrawWidth, HeartDrawHeight);
        var halfSize = new Vector2(HeartDrawWidth * 0.5f, HeartDrawHeight);

        for (var i = 0; i < PlayerHealth.HeartCount; i++)
        {
            var center = new Vector2(firstX + i * HeartStride, centerY);
            _ui.DrawMesh(_heartEmptyMesh, RectTransform(center, fullSize), Rgba.White, _heartEmptyTexture);

            var fill = Math.Clamp(hp - i * perHeart, 0f, perHeart);
            if (fill >= perHeart - 1e-3f)
                _ui.DrawMesh(_heartFullMesh, RectTransform(center, fullSize), Rgba.White, _heartTexture);
            else if (fill >= perHeart * 0.5f - 1e-3f)
                _ui.DrawMesh(_heartHalfMesh,
                    RectTransform(center with { X = center.X - HeartDrawWidth * 0.25f }, halfSize),
                    Rgba.White, _heartTexture);
        }
    }

    /// <summary>
    /// The air meter: <see cref="PlayerHealth.HeartCount"/> bubbles in a row over the hearts, right-aligned
    /// with them, one per tenth of a lung. A bubble fades with the air in it and an empty slot draws
    /// nothing, so the row shrinks as the player runs out. Hidden with a full lung or when dead.
    /// </summary>
    internal void DrawAir(float airShare, int health)
    {
        if (airShare >= 1f || health <= 0)
            return;
        var rowRight = _ui.ScreenSize.X * 0.5f
            + (HeartDrawWidth + (PlayerHealth.HeartCount - 1) * HeartStride) * 0.5f;
        var hotbarTop = _ui.ScreenSize.Y - BottomMargin - SlotSize;
        var centerY = hotbarTop - HeartGap - HeartDrawHeight - BubbleGap - BubbleDrawSize * 0.5f;
        var size = new Vector2(BubbleDrawSize);
        var bubbles = Math.Clamp(airShare, 0f, 1f) * PlayerHealth.HeartCount;
        for (var i = 0; i < PlayerHealth.HeartCount; i++)
        {
            var fill = Math.Clamp(bubbles - i, 0f, 1f);
            if (fill <= 0f)
                continue;
            // Index 0 is the rightmost: the row empties from the left, the way a lung runs out
            var center = new Vector2(rowRight - BubbleDrawSize * 0.5f - i * BubbleStride, centerY);
            _ui.DrawMesh(_bubbleMesh, RectTransform(center, size), Rgba.FromFloat(1f, 1f, 1f, fill), _bubbleTexture);
        }
    }

    /// <summary>
    /// The mana bar: a thin bar as wide as the hotbar row, centered in the margin under it, its blue
    /// fill growing from the left with <paramref name="manaShare"/>. Hidden while dead and in
    /// <paramref name="creative"/>, which casts for free.
    /// </summary>
    internal void DrawMana(float manaShare, int health, bool creative)
    {
        if (health <= 0 || creative)
            return;
        var width = HotbarLayout.RowWidth();
        var centerY = _ui.ScreenSize.Y - BottomMargin * 0.5f;
        var left = (_ui.ScreenSize.X - width) * 0.5f;
        _ui.DrawRect(new Vector2(_ui.ScreenSize.X * 0.5f, centerY), new Vector2(width, ManaBarHeight), ManaTrack);
        var fill = MathUtils.Clamp01(manaShare) * width;
        if (fill > 0.5f)
            _ui.DrawRect(new Vector2(left + fill * 0.5f, centerY), new Vector2(fill, ManaBarHeight), ManaFill);
    }

    /// <summary>
    /// The player's active status effects: one icon per bit of <paramref name="mask"/>
    /// (<see cref="StatusEffect.Bit"/>) whose effect names one, in id order, in a row ending just left
    /// of the hearts and centered on them, so it never meets the air bubbles, the item name or the
    /// toast stacked above the hearts. Hidden while dead or with no effect.
    /// </summary>
    internal void DrawEffects(byte mask, int health)
    {
        if (mask == 0 || health <= 0)
            return;
        var shown = 0;
        for (var id = 0; id < StatusEffect.MaxKinds; id++)
            if ((mask & (1 << id)) != 0 && _effectIcons[id] != 0)
                shown++;
        if (shown == 0)
            return;

        const float size = HudConfig.EffectIconSize;
        const float stride = size + HudConfig.EffectIconGap;
        var heartsLeft = (_ui.ScreenSize.X - (HeartDrawWidth + (PlayerHealth.HeartCount - 1) * HeartStride)) * 0.5f;
        var hotbarTop = _ui.ScreenSize.Y - BottomMargin - SlotSize;
        var centerY = hotbarTop - HeartGap - HeartDrawHeight * 0.5f;
        var x = heartsLeft - HudConfig.EffectIconGap - (shown - 1) * stride - size * 0.5f;
        for (var id = 0; id < StatusEffect.MaxKinds; id++)
        {
            if ((mask & (1 << id)) == 0 || _effectIcons[id] == 0)
                continue;
            _ui.DrawMesh(_bubbleMesh, RectTransform(new Vector2(x, centerY), new Vector2(size)), Rgba.White,
                _effectIcons[id]);
            x += stride;
        }
    }

    /// <summary>
    /// The Tab player list: a centered panel with a header and one line per online player (the local
    /// one first), drawn over the HUD while Tab is held.
    /// </summary>
    internal void DrawPlayerList(IReadOnlyList<string> names)
    {
        if (_ui.Font == null || names.Count == 0) return;

        var lineHeight = TextSize.Body + 6f;
        var width = PlayerListWidth;
        foreach (var name in names)
            width = MathF.Max(width, _ui.Font.MeasureSize(name, _ui.CountScale).X + 32f);
        var height = lineHeight * (names.Count + 1) + 16f;
        var center = new Vector2(_ui.ScreenSize.X * 0.5f, _ui.ScreenSize.Y * 0.28f);
        _ui.DrawRect(center, new Vector2(width, height), PanelBackground);

        var top = center.Y - height * 0.5f + 8f;
        _ui.DrawTextCentered($"Players ({names.Count})", center.X, top, _ui.CountScale, AccentGold);
        for (var i = 0; i < names.Count; i++)
        {
            _ui.DrawText(names[i], new Vector2(center.X - width * 0.5f + 16f, top + lineHeight * (i + 1)),
                _ui.CountScale, Rgba.White);
        }
    }

    /// <summary>
    /// One hover tooltip (see <see cref="ItemTooltip"/>): a panel just below-right of the cursor
    /// with the hovered item's lines, clamped inside the canvas.
    /// </summary>
    internal void DrawTooltip(Vector2 anchor, IReadOnlyList<string> lines, IReadOnlyDictionary<int, Rgba>? colors = null)
    {
        if (_ui.Font == null || lines.Count == 0) return;

        var lineHeight = TextSize.Body + 6f;
        var width = TooltipWidth;
        foreach (var line in lines)
            width = MathF.Max(width, _ui.Font.MeasureSize(line, _ui.CountScale).X + 24f);
        var height = lineHeight * lines.Count + 16f;
        var center = anchor + new Vector2(16f + width * 0.5f, 18f + height * 0.5f);
        center.X = Math.Clamp(center.X, width * 0.5f + 4f, _ui.ScreenSize.X - width * 0.5f - 4f);
        center.Y = Math.Clamp(center.Y, height * 0.5f + 4f, _ui.ScreenSize.Y - height * 0.5f - 4f);
        _ui.DrawRect(center, new Vector2(width, height), PanelBackground);
        _ui.DrawBorder(center, new Vector2(width, height), Border.Standard, SlotBorderColor);

        var top = center.Y - height * 0.5f + 8f;
        for (var i = 0; i < lines.Count; i++)
        {
            _ui.DrawText(lines[i], new Vector2(center.X - width * 0.5f + 12f, top + lineHeight * i),
                _ui.CountScale, colors != null && colors.TryGetValue(i, out var color) ? color : i == 0 ? Rgba.White : TextDim);
        }
    }

    // Smallest width of a tooltip panel; a long line widens it
    private const float TooltipWidth = 120f;

    /// <summary>
    /// One toast line (<see cref="HudToast"/>) centered a little above the item-name row, at
    /// <paramref name="alpha"/>. The chat log sits bottom-left, clear of the centered hotbar.
    /// </summary>
    internal void DrawToast(string text, float alpha)
    {
        var a = MathUtils.Clamp01(alpha);
        if (_ui.Font == null || a <= 0.004f || string.IsNullOrEmpty(text)) return;

        var heartsTop = HotbarLayout.Top(_ui.ScreenSize) - HeartGap - HeartDrawHeight;
        var top = heartsTop - HotbarNameGap - TextSize.Body - ToastRowGap;
        _ui.TextFade = a;
        var size = _ui.Font.MeasureSize(text, _ui.CountScale);
        _ui.DrawText(text, new Vector2((_ui.ScreenSize.X - size.X) * 0.5f, top), _ui.CountScale, Rgba.White);
        _ui.TextFade = 1f;
    }

    /// <summary>
    /// The selected item's name centered over the hearts row (see <see cref="HotbarNameFade"/>),
    /// at <paramref name="alpha"/>, fitted to the hotbar's width. The chat log sits bottom-left,
    /// clear of the centered hotbar, so the two never overlap.
    /// </summary>
    internal void DrawHotbarName(string name, float alpha)
    {
        var a = MathUtils.Clamp01(alpha);
        if (_ui.Font == null || a <= 0.004f || string.IsNullOrEmpty(name)) return;

        var heartsTop = HotbarLayout.Top(_ui.ScreenSize) - HeartGap - HeartDrawHeight;
        var top = heartsTop - HotbarNameGap - TextSize.Body;
        _ui.TextFade = a;
        try
        {
            _ui.DrawTextCenteredFitted(name, _ui.ScreenSize.X * 0.5f, top, _ui.CountScale, Rgba.White, HotbarLayout.RowWidth());
        }
        finally
        {
            _ui.TextFade = 1f;
        }
    }

    /// <summary>
    /// The held staff's spell slot line (see <see cref="StaffSlotIndicator"/>), drawn plain like the item
    /// name just right of the hotbar row and centered on it vertically. Shown in creative too, where the
    /// selected slot still decides the cast.
    /// </summary>
    internal void DrawStaffSlot(string label)
    {
        if (_ui.Font == null || string.IsNullOrEmpty(label)) return;

        var left = (_ui.ScreenSize.X + HotbarLayout.RowWidth()) * 0.5f + StaffSlotGap;
        var top = HotbarLayout.CenterY(_ui.ScreenSize) - TextSize.Body * 0.5f;
        _ui.DrawText(label, new Vector2(left, top), _ui.CountScale, Rgba.White);
    }

    internal void DrawFps(int fps) =>
        _ui.DrawText(_ui.TextCache.Fps(fps), new Vector2(ProfilerLeft, FpsTop), _ui.CountScale, Rgba.White);

    /// <summary>
    /// Draws the frame-timing table under the FPS text: a translucent panel sized to the block
    /// so it reads over bright terrain, then one <see cref="UIRenderer.DrawText"/> line per row. The lines
    /// are already formatted by <see cref="FrameProfiler"/> (rebuilt once a second), so this
    /// allocates nothing per frame.
    /// </summary>
    internal void DrawProfiler(IReadOnlyList<string> lines)
    {
        if (_ui.Font == null || lines.Count == 0) return;

        // Cap height of a glyph, so the spacing follows the font rather than a magic number
        var lineStep = _ui.Font.MeasureSize("X".AsSpan(), _ui.CountScale).Y * 1.5f;
        var top = FpsTop + lineStep;

        // Only the longest line is measured — the count font is monospaced, so character
        // count orders the rows by width, and measuring all of them every frame would cost
        // as much again as drawing them
        var longest = 0;
        for (var i = 1; i < lines.Count; i++)
            if (lines[i].Length > lines[longest].Length)
                longest = i;

        var size = new Vector2(
            _ui.Font.MeasureSize(lines[longest].AsSpan(), _ui.CountScale).X + ProfilerPadding * 2f,
            lines.Count * lineStep + ProfilerPadding * 2f);
        _ui.DrawRect(new Vector2(ProfilerLeft - ProfilerPadding + size.X * 0.5f,
            top - ProfilerPadding + size.Y * 0.5f), size, ProfilerBackground);

        for (var i = 0; i < lines.Count; i++)
            _ui.DrawText(lines[i], new Vector2(ProfilerLeft, top + i * lineStep), _ui.CountScale, Rgba.White);
    }

    /// <summary>Draws the hotbar centered along the bottom edge, highlighting the selected slot.</summary>
    /// <param name="inventory">Supplies the hotbar slots and the selected one.</param>
    /// <param name="headingDegrees">Compass heading (0 = north), picking heading-driven sheet frames.</param>
    /// <param name="timeSeconds">Scene clock, picking loop-driven sheet frames.</param>
    /// <param name="pops">Per-slot pickup pops scaling each icon and its count; null draws every slot at rest.</param>
    internal void DrawHotbar(Inventory inventory, float headingDegrees, float timeSeconds, HotbarPickupPop? pops = null)
    {
        var stride = SlotSize + SlotGap;
        var firstCenterX = HotbarLayout.FirstCenterX(_ui.ScreenSize);
        var centerY = HotbarLayout.CenterY(_ui.ScreenSize);
        var slotSize = new Vector2(SlotSize);

        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var center = new Vector2(firstCenterX + i * stride, centerY);
            var selected = i == inventory.SelectedIndex;

            _ui.DrawRect(center, slotSize, selected ? SelectedBackground : SlotBackground);
            _ui.DrawBorder(center, slotSize,
                selected ? Border.Selected : Border.Standard,
                selected ? SelectedBorderColor : SlotBorderColor);
            // A broken tool's slot flashes: a bright panel fading out (HotbarPickupPop.FlashAt)
            var flash = pops?.FlashAt(i) ?? 0f;
            if (flash > 0f)
                _ui.DrawRect(center, slotSize, new Rgba(255, 255, 255,
                    (byte)Math.Clamp(flash * HudConfig.HotbarFlashAlpha * 255f, 0f, 255f)));
        }

        // Icons come after every background so the depth clear can't erase a later slot's panel
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var id = inventory.IdAt(i);
            if (id == 0) continue;
            var center = new Vector2(firstCenterX + i * stride, centerY);
            // The pop marks a pickup; IconFill leaves enough margin for it to stay in the slot
            var iconSize = SlotSize * IconFill * (pops?.ScaleAt(i) ?? 1f);
            // Items show their flat sprites here like the list rows; blocks keep 3D cubes
            _ui.DrawIdIcon(center, iconSize, id, headingDegrees, timeSeconds);
        }

        // Durability bars sit along the slot bottom, under the icons so they stay visible.
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var slot = inventory.SlotAt(i);
            if (!slot.IsTool) continue;
            var fraction = inventory.ToolDurabilityFraction(slot.ToolInstance);
            if (fraction == null) continue;
            var center = new Vector2(firstCenterX + i * stride,
                centerY + SlotSize * 0.5f - DurabilityPadBottom - DurabilityHeight * 0.5f);
            _ui.DrawDurabilityBar(center, SlotSize * DurabilityFill, fraction.Value);
        }

        // Counts overlay the slot's bottom-right corner, after the icons so text is on top.
        // Slots share pool totals, so two slots showing one block show the same number.
        if (_ui.Font != null)
        {
            for (var i = 0; i < Inventory.SlotCount; i++)
            {
                if (inventory.IdAt(i) == 0) continue;
                var text = _ui.TextCache.SlotCount(i, inventory.CountAt(i));
                // The count pops with its icon, growing out of the slot's corner
                var countScale = _ui.CountScale * (pops?.ScaleAt(i) ?? 1f);
                var size = _ui.Font.MeasureSize(text.AsSpan(), countScale);
                var slotRight = firstCenterX + i * stride + SlotSize * 0.5f;
                var slotBottom = centerY + SlotSize * 0.5f;
                _ui.DrawText(text,
                    new Vector2(slotRight - CountPadRight - size.X, slotBottom - CountPadBottom - size.Y),
                    countScale, Rgba.White);
            }
        }
    }

    /// <summary>
    /// Draws every collected drop's icon in flight: from where the drop was (its world position
    /// projected through <paramref name="viewProjection"/>) arcing into its hotbar slot. A drop
    /// behind the camera starts from the bottom-center of the screen.
    /// </summary>
    internal void DrawFlyingPickups(PickupFlyIcons flying, Matrix4x4 viewProjection, float headingDegrees, float timeSeconds)
    {
        var screen = _ui.ScreenSize;
        for (var i = 0; i < flying.Count; i++)
        {
            var icon = flying.At(i);
            var clip = Vector4.Transform(new Vector4(icon.WorldPosition, 1f), viewProjection);
            var start = clip.W > 0.001f
                ? new Vector2((clip.X / clip.W * 0.5f + 0.5f) * screen.X, (0.5f - clip.Y / clip.W * 0.5f) * screen.Y)
                : new Vector2(screen.X * 0.5f, screen.Y);
            var progress = PickupFlyIcons.Progress(icon);
            var position = PickupFlyIcons.Arc(start, HotbarLayout.SlotCenter(icon.Slot, screen), progress);
            _ui.DrawIdIcon(position, SlotSize * IconFill * PickupFlyIcons.ScaleAt(progress), icon.Id,
                headingDegrees, timeSeconds);
        }
    }

    /// <summary>Hotbar slot under a window-pixel point (top-left origin), if any.</summary>
    internal int? HotbarSlotAt(Vector2 point)
    {
        point = _ui.ToCanvas(point);
        return HotbarLayout.SlotAt(point, _ui.ScreenSize);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_crosshairTexture);
        _gl.DeleteTexture(_heartTexture);
        _gl.DeleteTexture(_heartEmptyTexture);
        _gl.DeleteTexture(_bubbleTexture);
        foreach (var icon in _effectIcons)
            if (icon != 0)
                _gl.DeleteTexture(icon);
        _bubbleMesh.Dispose();
        _heartEmptyMesh.Dispose();
        _heartFullMesh.Dispose();
        _heartHalfMesh.Dispose();
        _crosshairCenterMesh.Dispose();
        foreach (var arm in _crosshairArmMeshes)
            arm.Dispose();
    }
}
