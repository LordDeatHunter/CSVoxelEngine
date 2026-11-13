using System;
using System.Globalization;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.SpellBuilder;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The spell builder panel the spell table opens: the title, one button per slot of the held staff
/// (the chosen one highlighted, bound ones marked gold), the four part columns of
/// <see cref="SpellBuilderCatalog.Columns"/> with the picked parts highlighted, the live stats of the
/// selection compiled against the staff's tags, and the CLEAR and BIND buttons. Every rectangle comes from
/// <see cref="SpellBuilderLayout"/>, which the click handling reads too, so a click lands on exactly what was
/// drawn. The stat strings are cached on the view's <see cref="SpellBuilderView.Version"/> and the staff, so a
/// steady frame formats nothing. Draws through the <see cref="UIRenderer"/> primitives. Render thread only.
/// </summary>
internal sealed class SpellBuilderRenderer
{
    private const float LineHeight = 26f;

    private static readonly string[] ColumnHeaders = ["Element", "Shape", "Modifiers", "Trigger"];
    private const string NoneKnown = "none known";

    // "1".."StaffMaxSlots", built once: the slot buttons never format a label.
    private static readonly string[] SlotLabels = BuildSlotLabels(SpellConfig.StaffMaxSlots);

    private readonly UIRenderer _ui;

    // The formatted stats of the last (view version, staff) pair drawn.
    private int _statsVersion = -1;
    private StaffItem? _statsStaff;
    private string _verdictText = "";
    private string _costText = "";
    private string _factorText = "";

    internal SpellBuilderRenderer(UIRenderer ui)
    {
        _ui = ui;
    }

    /// <summary>
    /// The whole panel for <paramref name="view"/>'s selection and the staff <paramref name="inventory"/> holds,
    /// or, without a staff in hand, the panel with <see cref="SpellBuilderText.HoldStaffHint"/>.
    /// </summary>
    internal void DrawSpellBuilder(Inventory inventory, SpellBuilderView view)
    {
        var canvas = _ui.ScreenSize;
        var panel = SpellBuilderLayout.PanelRect(canvas);
        _ui.DrawRect(panel.Center, panel.Size, PanelBackground);
        _ui.DrawBorder(panel.Center, panel.Size, Border.Standard, SlotBorderColor);
        _ui.DrawRect(new Vector2(panel.Center.X, panel.Center.Y - panel.Size.Y * 0.5f + 3f),
            new Vector2(panel.Size.X - 4f, 2f), AccentGold);
        _ui.DrawText(SpellBuilderText.Title, SpellBuilderLayout.TitleOrigin(canvas), _ui.HeadScale, Rgba.White);

        if (!HeldStaff.TryGet(inventory, out var staff, out var stack))
        {
            _ui.DrawTextMiddle(SpellBuilderText.HoldStaffHint, panel.Center, _ui.CountScale, TextDim);
            return;
        }

        var component = stack.Get<StaffComponent>();
        DrawSlots(staff, component, view, canvas);
        DrawColumns(view, canvas);

        var stats = view.StatsFor(staff);
        DrawStats(view, staff, stats, canvas);
        _ui.DrawButton(AsRect(SpellBuilderLayout.ClearRect(canvas)), "CLEAR", component.IsBound(view.Slot), DangerRed);
        _ui.DrawButton(AsRect(SpellBuilderLayout.BindRect(canvas)), "BIND", stats.IsValid, ReadyGreen);
    }

    private void DrawSlots(StaffItem staff, in StaffComponent component, SpellBuilderView view, Vector2 canvas)
    {
        var count = Math.Min(staff.Slots, SlotLabels.Length);
        for (var slot = 0; slot < count; slot++)
        {
            var (center, size) = SpellBuilderLayout.SlotRect(slot, count, canvas);
            var chosen = slot == view.Slot;
            var bound = component.IsBound(slot);
            _ui.DrawRect(center, size, chosen ? SelectedBackground : SlotBackground);
            _ui.DrawBorder(center, size, Border.Standard,
                chosen ? SelectedBorderColor : bound ? AccentGold : SlotBorderColor);
            _ui.DrawTextMiddle(SlotLabels[slot], center, _ui.CountScale,
                bound ? AccentGold : chosen ? Rgba.White : TextDim);
        }
    }

    private void DrawColumns(SpellBuilderView view, Vector2 canvas)
    {
        for (var column = 0; column < SpellBuilderCatalog.Columns.Length; column++)
        {
            var header = SpellBuilderLayout.HeaderCenter(column, canvas);
            _ui.DrawTextMiddle(ColumnHeaders[column], header, _ui.CountScale, AccentGold);

            var parts = view.Column(SpellBuilderCatalog.Columns[column]);
            if (parts.Count == 0)
            {
                var (emptyCenter, _) = SpellBuilderLayout.PartRect(column, 0, canvas);
                _ui.DrawTextMiddle(NoneKnown, emptyCenter, _ui.CountScale, TextDim);
                continue;
            }

            var rows = Math.Min(parts.Count, SpellBuilderLayout.MaxRowsPerColumn);
            for (var row = 0; row < rows; row++)
            {
                var part = parts[row];
                var (center, size) = SpellBuilderLayout.PartRect(column, row, canvas);
                var selected = view.IsSelected(part);
                _ui.DrawRect(center, size, selected ? SelectedBackground : SlotBackground);
                if (selected)
                    _ui.DrawBorder(center, size, Border.Standard, SelectedBorderColor);
                _ui.DrawTextMiddle(_ui.FittedText(part.DisplayName, _ui.CountScale, size.X - 12f),
                    center, _ui.CountScale, selected ? Rgba.White : TextDim);
            }
        }
    }

    private void DrawStats(SpellBuilderView view, StaffItem staff, in SpellStats stats, Vector2 canvas)
    {
        if (_statsVersion != view.Version || !ReferenceEquals(_statsStaff, staff))
        {
            _verdictText = SpellBuilderText.Verdict(stats.Verdict);
            _costText = string.Create(CultureInfo.InvariantCulture,
                $"Cost {(int)MathF.Round(stats.Cost)} mana   Fizzle {(int)MathF.Round(stats.FizzleChance * 100f)}%");
            _factorText = string.Create(CultureInfo.InvariantCulture,
                $"Damage x{stats.DamageFactor:0.0}   Range x{stats.RangeFactor:0.0}");
            _statsVersion = view.Version;
            _statsStaff = staff;
        }

        var origin = SpellBuilderLayout.StatsOrigin(canvas);
        _ui.DrawText(_verdictText, origin, _ui.CountScale, stats.IsValid ? ReadyGreen : DangerRed);
        _ui.DrawText(_costText, origin + new Vector2(0f, LineHeight), _ui.CountScale, Rgba.White);
        _ui.DrawText(_factorText, origin + new Vector2(0f, LineHeight * 2f), _ui.CountScale, Rgba.White);
    }

    private static UiRect AsRect((Vector2 Center, Vector2 Size) rect) => new(rect.Center, rect.Size);

    private static string[] BuildSlotLabels(int count)
    {
        var labels = new string[count];
        for (var i = 0; i < count; i++)
            labels[i] = (i + 1).ToString(CultureInfo.InvariantCulture);
        return labels;
    }
}
