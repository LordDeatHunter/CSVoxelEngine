using System;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.Forge;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The anvil's recipe list, in the crafting card's dress: the panel frame with its gold accent, the title, a line naming
/// the metal stacked on the anvil, a grid of recipe cards (the result's icon and rarity-colored name, the metal it takes)
/// with a hover look, and CLOSE. Every rectangle comes from <see cref="ForgeLayout"/>, which the clicks read too. Draws
/// through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class ForgeRenderer
{
    private const float CardIcon = 36f;
    private const float Margin = 10f;

    private readonly UIRenderer _ui;

    internal ForgeRenderer(UIRenderer ui) => _ui = ui;

    /// <summary>Draws the list <paramref name="view"/> holds; <paramref name="time"/> and
    /// <paramref name="headingDegrees"/> pick an animated icon's frame.</summary>
    internal void Draw(ForgeView view, float time, float headingDegrees)
    {
        var canvas = _ui.ScreenSize;
        _ui.DrawPanelFrame(ForgeLayout.Panel(canvas), CardBackground);

        var title = ForgeLayout.Title(canvas);
        _ui.DrawTextCenteredY("Anvil", title.Left, title.Center.Y, _ui.HeadScale, AccentGold);

        var metal = Inventory.DisplayName(view.MetalId);
        var line = ForgeLayout.StackLine(canvas);
        _ui.DrawTextCenteredY(_ui.FittedText($"{metal} x{view.MetalCount} on the anvil", _ui.CountScale, line.Size.X),
            line.Left, line.Center.Y, _ui.CountScale, TextDim);

        var recipes = view.Recipes;
        if (recipes.Count == 0)
        {
            var grid = ForgeLayout.Grid(canvas);
            _ui.DrawTextCenteredFitted($"Not enough {metal} for any recipe yet: stack more on the anvil", grid.Center.X,
                grid.Top + 12f, _ui.CountScale, TextDim, grid.Size.X);
        }
        else
        {
            // The icons draw with depth; the pass's one depth clear has to come first
            _ui.EnsureDepthCleared();
            var shown = Math.Min(recipes.Count, ForgeLayout.Capacity(canvas));
            for (var i = 0; i < shown; i++)
                DrawCard(ForgeLayout.Card(i, canvas), i, recipes[i], metal, view.Hover, time, headingDegrees);
        }

        DrawCloseButton(ForgeLayout.CloseButton(canvas), view.Hover);
    }

    private void DrawCard(UiRect card, int index, ForgeRecipe recipe, string metal, Vector2? hover, float time,
        float headingDegrees)
    {
        var hovered = hover is { } p && card.Contains(p);
        _ui.DrawRect(card.Center, card.Size, hovered ? HoverBackground : SlotBackground);

        var style = RarityStyle.Of(Rarities.Of(recipe.ResultId, null));
        _ui.Chrome.DrawRow(card.Center, card.Size, style, time, index);
        _ui.DrawRect(new Vector2(card.Left + 2f, card.Center.Y), new Vector2(4f, card.Size.Y), style.AccentOr(SlotBorderColor));
        if (hovered)
            _ui.DrawBorder(card.Center, card.Size, Border.Thin, AccentGold);

        var iconCenter = new Vector2(card.Left + Margin + CardIcon * 0.5f, card.Center.Y);
        _ui.DrawIdIcon(iconCenter, CardIcon, recipe.ResultId, headingDegrees, time);

        var textLeft = iconCenter.X + CardIcon * 0.5f + Margin;
        var textWidth = card.Right - Margin - textLeft;
        var name = Inventory.DisplayName(recipe.ResultId);
        if (recipe.ResultCount > 1) name += $" x{recipe.ResultCount}";
        _ui.DrawTextCenteredY(_ui.FittedText(name, _ui.CountScale, textWidth), textLeft, card.Center.Y - 11f,
            _ui.CountScale, style.Color);
        _ui.DrawTextCenteredY(_ui.FittedText($"{recipe.MetalCount} {metal}", _ui.CountScale, textWidth), textLeft,
            card.Center.Y + 11f, _ui.CountScale, TextDim);
    }

    private void DrawCloseButton(UiRect rect, Vector2? hover)
    {
        var hovered = hover is { } p && rect.Contains(p);
        _ui.DrawRect(rect.Center, rect.Size, hovered ? ButtonHover : ChipBackground);
        _ui.DrawBorder(rect.Center, rect.Size, Border.Standard, SlotBorderColor);
        _ui.DrawTextMiddle("CLOSE", rect.Center, _ui.CountScale, Rgba.White);
    }
}
