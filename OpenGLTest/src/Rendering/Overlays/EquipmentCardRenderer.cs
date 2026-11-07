using System;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The equipment card the [E] panel's Gear tab shows: one row per equipment slot with the worn piece's icon and
/// name, and the summed stats under them. Geometry is <see cref="EquipmentCardLayout"/>, which the click handling
/// reads too. Draws through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class EquipmentCardRenderer
{
    private const float SlotIcon = 20f;
    private const float LabelCapHeight = 11f;

    private readonly UIRenderer _ui;

    internal EquipmentCardRenderer(UIRenderer ui) => _ui = ui;

    /// <summary>Draws the card for the player's equipment. <paramref name="selectedInstance"/> is the gear row picked
    /// in the list, whose fitting slots light up as the places a click would put it.</summary>
    internal void Draw(Inventory inventory, int selectedInstance, Vector2? hover, float time, float headingDegrees)
    {
        var screen = _ui.ScreenSize;
        var card = EquipmentCardLayout.Card(screen);
        _ui.DrawPanelFrame(card, PanelBackground);

        var content = PanelFrameLayout.Standard(card).Content;
        _ui.DrawTextCenteredFitted("Equipment", content.Center.X, content.Top + 4f, _ui.HeadScale, Rgba.White,
            content.Size.X);

        var equipment = inventory.Equipment;
        // Slot labels are a size under the body text, so a column of them stays narrow
        var labelScale = _ui.Font?.ScaleForCapHeight(LabelCapHeight) ?? _ui.CountScale;
        var selectedKind = inventory.TryGetToolStack(selectedInstance, out var picked) && picked.Item is GearItem gear
            ? gear.Info.Slot
            : (GearSlot?)null;

        for (var slot = 0; slot < GearConfig.EquipmentSlotCount; slot++)
        {
            var row = EquipmentCardLayout.SlotRow(slot, screen);
            var worn = equipment.StackAt(slot);
            var fits = selectedKind is { } kind && GearSlots.Fits(kind, slot);
            var hovered = hover is { } p && row.Contains(p);
            _ui.DrawRect(row.Center, row.Size,
                fits ? SelectedBackground : hovered ? HoverBackground : SlotBackground);
            if (fits)
                _ui.DrawBorder(row.Center, row.Size, Border.Thin, ReadyGreen);

            var label = GearSlots.Label(GearSlots.KindOf(slot));

            _ui.DrawTextCenteredY(_ui.FitText(label, labelScale, EquipmentCardLayout.LabelColumnWidth - 10f), row.Left + 6f,
                row.Center.Y, labelScale, worn == null ? TextDim : AccentGold);

            if (worn == null) continue;
            var iconCenter = new Vector2(row.Left + EquipmentCardLayout.LabelColumnWidth + SlotIcon * 0.5f, row.Center.Y);
            _ui.DrawIdIcon(iconCenter, SlotIcon, worn.DefinitionId, headingDegrees, time);
            var nameLeft = iconCenter.X + SlotIcon * 0.5f + 6f;
            _ui.DrawTextCenteredY(_ui.FitText(worn.ResolvedName, _ui.CountScale, row.Right - nameLeft - 6f),
                nameLeft, row.Center.Y, _ui.CountScale, Rgba.White);
        }

        var lines = GearText.Lines(equipment.Stats);
        var origin = EquipmentCardLayout.StatsTopLeft(screen);
        if (lines.Count == 0)
        {
            _ui.DrawTextCenteredY("No gear worn", origin.X, origin.Y + EquipmentCardLayout.StatLineHeight * 0.5f,
                _ui.CountScale, TextDim);
            return;
        }
        for (var i = 0; i < Math.Min(lines.Count, EquipmentCardLayout.StatLines); i++)
            _ui.DrawTextCenteredY(_ui.FitText(lines[i], _ui.CountScale, content.Size.X), origin.X,
                origin.Y + (i + 0.5f) * EquipmentCardLayout.StatLineHeight, _ui.CountScale, ReadyGreen);
    }
}
