using System;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.InventoryPanel;
using static VoxelEngine.Rendering.Overlays.UIRenderer;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The crafting card of the [E] panel's Craft tab: its draw and its hit-tests, which share the one
/// layout cache, so a click reads exactly the geometry that was drawn. Draws through the
/// <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class CraftCardRenderer
{
    private static readonly Rgba ButtonReady = new(232, 184, 72);
    private static readonly Rgba ButtonDisabled = Rgba.Gray(0.12f, 0.9f);
    private static readonly Rgba ButtonTextReady = new(30, 26, 20);
    private static readonly Rgba ButtonTextDisabled = Rgba.Gray(0.45f, 1f);

    private readonly GL _gl;
    private readonly UIRenderer _ui;

    // Crafting card text measures, bound once in the constructor so neither the cache nor a
    // frame ever allocates a delegate; the "Batch" label is measured once for the same reason.
    private readonly TextMeasure _measureHead;
    private readonly TextMeasure _measureBody;
    private readonly float _batchLabelWidth;
    // The crafting card's wrapped layout, rebuilt only when its screen, recipe or status changes;
    // its Current is also what CraftHitAt hits, so a click reads the geometry that was drawn.
    private readonly CraftCardLayoutCache _craftLayouts;

    internal CraftCardRenderer(GL gl, UIRenderer ui)
    {
        _gl = gl;
        _ui = ui;

        if (_ui.Font is { } font)
        {
            var headScale = _ui.HeadScale;
            var countScale = _ui.CountScale;
            _measureHead = s => font.MeasureSize(s, headScale).X;
            _measureBody = s => font.MeasureSize(s, countScale).X;
            _batchLabelWidth = font.MeasureSize("Batch".AsSpan(), countScale).X;
        }
        else
        {
            // No font: everything lays out as one line, and DrawText draws nothing anyway.
            _measureHead = static _ => 0f;
            _measureBody = static _ => 0f;
            _batchLabelWidth = 44f;
        }
        _craftLayouts = new CraftCardLayoutCache(_measureHead, _measureBody, _batchLabelWidth);
    }

    /// <summary>
    /// This frame's crafting card layout, or null (and the cache cleared, so a stale layout is
    /// never hit-tested) when no recipe is open.
    /// </summary>
    private CraftCardLayout? CraftLayoutFor(Inventory inventory, InventoryView view)
    {
        if (view.Craft.Recipe is not { } recipe)
        {
            _craftLayouts.Clear();
            return null;
        }
        return _craftLayouts.Get(_ui.ScreenSize, recipe, view.Craft.StatusLine(inventory));
    }

    /// <summary>
    /// Which crafting control sits under a window-pixel point, if any. Hits the layout that was
    /// last drawn, so a click can never disagree with the pixels; a layout that no longer matches
    /// the open recipe or the canvas size hits nothing.
    /// </summary>
    internal CraftHit CraftHitAt(Vector2 point, InventoryView view)
    {
        point = _ui.ToCanvas(point);
        var layout = _craftLayouts.Current;
        if (layout == null || view.Craft.Recipe == null
            || !ReferenceEquals(layout.Recipe, view.Craft.Recipe) || layout.Screen != _ui.ScreenSize)
            return new CraftHit(CraftHitKind.None);
        return layout.HitAt(point, layout.ClampScroll(view.CraftScrollOffset));
    }

    /// <summary>Whether a window-pixel point lies on the last drawn crafting card.</summary>
    internal bool IsOverCraftCard(Vector2 windowPoint) =>
        _craftLayouts.Current?.Contains(_ui.ToCanvas(windowPoint)) ?? false;

    /// <summary>Clamps a crafting card scroll offset against the last drawn layout (0 without one).</summary>
    internal float ClampCraftScroll(float offset) => _craftLayouts.Current?.ClampScroll(offset) ?? 0f;

    private void DrawBoxButton(Vector2 center, float size, string label, Rgba bg, Rgba border, Rgba text)
    {
        _ui.DrawRect(center, new Vector2(size), _ui.IsHovered(center, new Vector2(size)) ? ButtonHover : bg);
        _ui.DrawBorder(center, new Vector2(size), Border.Thin, border);
        _ui.DrawTextMiddle(label, center, _ui.CountScale, text);
    }

    internal void DrawCraftDetail(Inventory inventory, InventoryView view, float time, float headingDegrees)
    {
        const float top = CraftCardLayout.Top;
        var layout = CraftLayoutFor(inventory, view);
        if (layout == null)
        {
            var width = CraftCardLayout.WidthFor(_ui.ScreenSize);
            var center = new Vector2(CraftCardLayout.LeftFor(_ui.ScreenSize) + width * 0.5f,
                top + CraftCardLayout.EmptyHeight * 0.5f);
            var size = new Vector2(width, CraftCardLayout.EmptyHeight);
            _ui.DrawRect(center, size, PanelBackground);
            _ui.DrawBorder(center, size, Border.Standard, SlotBorderColor);
            _ui.DrawRect(new Vector2(center.X, top + 3f), new Vector2(width - 4f, 2f), AccentGold);
            var avail = CraftCardLayout.InnerWidthFor(_ui.ScreenSize) - 8f;
            _ui.DrawTextCenteredFitted("Crafting", center.X, top + CraftCardLayout.Pad, _ui.HeadScale, Rgba.White, avail);
            _ui.DrawTextCenteredFitted("Pick a recipe on the left", center.X,
                top + CraftCardLayout.Pad + 34f, _ui.CountScale, TextDim, avail);
            _ui.DrawTextCenteredFitted("then feed it items and hit CRAFT", center.X,
                top + CraftCardLayout.Pad + 54f, _ui.CountScale, TextDim, avail);
            return;
        }

        var scroll = layout.ClampScroll(view.CraftScrollOffset);
        _ui.DrawRect(layout.CardCenter, layout.CardSize, PanelBackground);
        _ui.DrawBorder(layout.CardCenter, layout.CardSize, Border.Standard, SlotBorderColor);
        _ui.DrawRect(new Vector2(layout.CardCenter.X, top + 3f), new Vector2(layout.Width - 4f, 2f), AccentGold);

        if (layout.Overflows)
        {
            // Same idiom as DrawInventoryPanel's row loop: DrawInventoryPanel already ran
            // EnsureDepthCleared before its own list scissor, so the spinning icons' latched
            // depth clear never happens under this scissor. Nothing may return from inside.
            var (sx, sy, sw, sh) = UiCanvas.ScissorRect(
                new Vector2(layout.Left, top + 2f) + _ui.OverlayOffset, new Vector2(layout.Width, layout.ViewHeight - 4f),
                _ui.Scale, _ui.WindowSize, _ui.FramebufferSize);
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        }
        try
        {
            DrawCraftContent(inventory, view, layout, scroll, time, headingDegrees);
        }
        finally
        {
            if (layout.Overflows) _gl.Disable(EnableCap.ScissorTest);
        }

        if (layout.Overflows)
        {
            var track = layout.TrackRect();
            _ui.DrawRect(track.Center, track.Size, SlotBackground);
            var thumb = layout.ThumbRect(scroll);
            _ui.DrawRect(thumb.Center, thumb.Size, SlotBorderColor);
        }
    }

    /// <summary>The crafting card's content, every Y read from the layout and offset by the scroll.</summary>
    private void DrawCraftContent(Inventory inventory, InventoryView view, CraftCardLayout layout,
        float scroll, float time, float headingDegrees)
    {
        const float pad = CraftCardLayout.Pad;
        const float ingPad = CraftCardLayout.IngPad;
        var session = view.Craft;
        var recipe = layout.Recipe;
        var ready = session.IsReady(inventory);
        var cardCenterX = layout.CardCenter.X;
        var innerWidth = layout.InnerWidth;

        var iconCenter = layout.IconCenter(scroll);
        var spin = time * 0.9f;
        if (ItemRegistry.IsItem(recipe.ResultId))
            _ui.DrawItemIcon(iconCenter, 56f, recipe.ResultId, spin);
        else
            _ui.DrawBlockIconSpinning(iconCenter, 60f, recipe.ResultId, spin);

        var textX = layout.TextX;
        var titleY = layout.ContentY(layout.TitleY, scroll);
        for (var k = 0; k < layout.TitleLines.Length; k++)
            _ui.DrawText(layout.TitleLines[k], new Vector2(textX, titleY + k * CraftCardLayout.TitleLineH),
                _ui.HeadScale, Rgba.White);
        var makesY = layout.ContentY(layout.MakesY, scroll);
        for (var k = 0; k < layout.MakesLines.Length; k++)
            _ui.DrawText(layout.MakesLines[k], new Vector2(textX, makesY + k * CraftCardLayout.BodyLineH),
                _ui.CountScale, TextDim);

        var batchTextY = layout.ContentY(layout.BatchTextY, scroll);
        _ui.DrawText("Batch", new Vector2(textX, batchTextY), _ui.CountScale, TextDim);
        var batchMinus = layout.BatchMinusCenter(scroll);
        var batchPlus = layout.BatchPlusCenter(scroll);
        DrawBoxButton(batchMinus, CraftCardLayout.StepBox, "-", ChipBackground, SlotBorderColor, Rgba.White);
        _ui.DrawTextMiddle(session.Batch.ToString(), layout.BatchCountCenter(scroll) with { Y = batchMinus.Y },
            _ui.CountScale, Rgba.White);
        DrawBoxButton(batchPlus, CraftCardLayout.StepBox, "+", ChipBackground, SlotBorderColor, Rgba.White);
        var maxBatch = session.MaxAffordable(inventory);
        var maxX = layout.BatchMaxX(scroll);
        var maxRight = layout.Left + layout.Width - pad;
        _ui.DrawText(_ui.FitText($"max {maxBatch}", _ui.CountScale, Math.Max(20f, maxRight - maxX)),
            new Vector2(maxX, batchTextY),
            _ui.CountScale, maxBatch >= session.Batch ? ReadyGreen : DangerRed);

        var separatorY = layout.ContentY(pad + layout.HeaderHeight + CraftCardLayout.HeaderGap - 5f, scroll);
        _ui.DrawRect(new Vector2(cardCenterX, separatorY), new Vector2(innerWidth, Border.Thin), SlotBorderColor);

        var ingLeft = layout.Left + pad + ingPad;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ing = layout.Ingredients[i];
            var cardTop = layout.ContentY(ing.Top, scroll);
            _ui.DrawRect(new Vector2(cardCenterX, cardTop + ing.Height * 0.5f),
                new Vector2(innerWidth, ing.Height), CardBackground);

            var need = session.NeedAt(i);
            var fed = session.FedAt(i);
            var ingTitleY = layout.ContentY(ing.TitleY, scroll);
            var titleLines = layout.IngredientTitleLines[i];
            for (var k = 0; k < titleLines.Length; k++)
                _ui.DrawText(titleLines[k], new Vector2(ingLeft, ingTitleY + k * CraftCardLayout.IngTitleLineH),
                    _ui.CountScale, Rgba.White);

            var barY = layout.ContentY(ing.BarY, scroll);
            var barW = innerWidth - ingPad * 2f;
            _ui.DrawRect(new Vector2(ingLeft + barW * 0.5f, barY + CraftCardLayout.IngBarH * 0.5f),
                new Vector2(barW, CraftCardLayout.IngBarH), SlotBackground);
            var fill = MathUtils.Clamp01(need > 0 ? fed.Count / (float)need : 1f);
            if (fill > 0f)
                _ui.DrawRect(new Vector2(ingLeft + barW * fill * 0.5f, barY + CraftCardLayout.IngBarH * 0.5f),
                    new Vector2(barW * fill, CraftCardLayout.IngBarH), fill >= 1f ? ReadyGreen : AccentGold);

            var fedY = layout.ContentY(ing.FedY, scroll);
            var fedCx = ingLeft + 16f;
            var fedCy = fedY + CraftCardLayout.IngFedH * 0.5f;
            if (fed.MemberId != 0)
                _ui.DrawIdIcon(new Vector2(fedCx, fedCy), 30f, fed.MemberId, headingDegrees, time);
            else
                _ui.DrawTextMiddle("?", new Vector2(fedCx, fedCy), _ui.CountScale, TextDim);

            var minus = layout.IngMinusCenter(i, scroll);
            var plus = layout.IngPlusCenter(i, scroll);
            var fedText = $"{fed.Count} / {need} fed";
            var fedAvail = minus.X - CraftCardLayout.IngStepBox * 0.5f - (fedCx + 24f) - 8f;
            _ui.DrawTextCenteredY(_ui.FitText(fedText, _ui.CountScale, Math.Max(20f, fedAvail)),
                fedCx + 24f, fedCy, _ui.CountScale,
                fed.Count >= need ? ReadyGreen : Rgba.White);
            DrawBoxButton(minus, CraftCardLayout.IngStepBox, "-", ChipBackground, SlotBorderColor, Rgba.White);
            DrawBoxButton(plus, CraftCardLayout.IngStepBox, "+", ChipBackground, SlotBorderColor, Rgba.White);

            var members = recipe.Ingredients[i].AcceptedIds;
            var picked = fed.MemberId;
            const float chipSize = CraftCardLayout.ChipSize;
            for (var c = 0; c < ing.MemberCount; c++)
            {
                var chipC = layout.ChipCenter(i, c, scroll);
                var owned = inventory.CountFor(members[c]);
                var isPicked = picked != 0 && picked == members[c];
                _ui.DrawRect(chipC, new Vector2(chipSize), owned > 0 ? ChipBackground : SlotBackground);
                _ui.DrawBorder(chipC, new Vector2(chipSize), isPicked ? Border.Selected : Border.Thin,
                    isPicked ? AccentGold : SlotBorderColor);
                _ui.DrawIdIcon(chipC with { Y = chipC.Y - 3f }, 26f, members[c], headingDegrees, time);
                var ownedText = owned.ToString();
                if (_ui.Font != null)
                {
                    var size = _ui.Font.MeasureSize(ownedText.AsSpan(), _ui.CountScale);
                    _ui.DrawText(ownedText,
                        new Vector2(chipC.X + chipSize * 0.5f - 3f - size.X,
                            chipC.Y + chipSize * 0.5f - 3f - size.Y),
                        _ui.CountScale, owned > 0 ? Rgba.White : TextDim);
                }
            }
        }

        var statusY = layout.ContentY(layout.StatusY, scroll);
        var statusColor = ready ? ReadyGreen : AccentGold;
        for (var k = 0; k < layout.StatusLines.Length; k++)
            _ui.DrawTextCentered(layout.StatusLines[k], cardCenterX, statusY + k * CraftCardLayout.StatusLineH,
                _ui.CountScale, statusColor);

        var buttonC = layout.ButtonCenter(scroll);
        var buttonS = layout.ButtonSize;
        var total = recipe.ResultCount * session.Batch;
        _ui.DrawRect(buttonC, buttonS, ready ? ButtonReady : ButtonDisabled);
        _ui.DrawBorder(buttonC, buttonS, Border.Standard, ready ? Rgba.White : SlotBorderColor);
        _ui.DrawTextMiddle(session.Batch > 1 ? $"CRAFT  x{total}" : "CRAFT",
            buttonC, _ui.HeadScale,
            ready ? ButtonTextReady : ButtonTextDisabled);
    }
}
