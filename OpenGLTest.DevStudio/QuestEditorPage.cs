using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.App;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Rendering.Overlays;
using VoxelEngine.Saves;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.Quests;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.DevStudio;

/// <summary>
/// DevStudio's QUESTS tab over a <see cref="QuestEditorState"/>: the quests drawn exactly as the in-game book draws them
/// (<see cref="QuestBookRenderer.DrawGraph"/>) on the canvas between the side panels, a top bar (ADD QUEST, UNDO, REDO,
/// SAVE ALL, REVERT ALL), and the selected quest's fields and requirements in the right
/// panel. UNDO / REDO (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z) step through <see cref="QuestEditorState"/>'s history, where
/// one typed field or one drag is one step (the page names the gesture in <see cref="QuestEditorState.Gesture"/>).
/// SAVE ALL / REVERT ALL (a second click within <see cref="ConfirmSeconds"/> confirms) act on every quest file,
/// SAVE QUEST / REVERT QUEST on the selected quest alone; a quest with unsaved edits carries a gold corner mark.
/// Clicking a node selects it, Ctrl+click adds it to the selection (or takes it out), dragging moves it (shift-drag
/// moves every quest linked to it, Alt turns snapping off; dragging any quest of a multi-selection moves them all), shift-drag on empty canvas draws a box that selects every quest in it,
/// clicking a line selects that link (its panel turns it around or deletes it), a right-drag from one node to another makes the second depend on the first (dropped on empty canvas, it adds a
/// quest there that depends on the first), a double-click on empty canvas adds a quest
/// there, dragging empty canvas pans and the wheel zooms. A grid click picks the quest's icon, or the target of a
/// requirement whose PICK is armed; dragging a grid slot onto the canvas adds a quest there with that icon, named
/// after it. Free text labels (ADD LABEL, or a shift-double-click on empty canvas) are clicked to select, dragged to
/// move and edited in the right panel (text with \n for a line break, SIZE, COLOR). With several quests selected, the right panel lists them instead (icon and title, scrolled by the wheel
/// when too long; a click on a row opens that quest alone) over a DELETE button. A requirement's type and the quest's reveal mode (SHOW) are <see cref="DropdownState"/>s. Geometry is rebuilt each draw and hit-tested on the next mouse-down.
/// </summary>
internal sealed class QuestEditorPage(QuestEditorState state, TextFieldKeys keys, QuestIconPicker icons)
{
    private enum Field { Id, Title, Description, Target, Label, LabelText, IconSearch }

    private enum Action
    {
        AddQuest, AddLabel, Save, RevertAll, Undo, Redo, SaveLabel, RevertLabel, DeleteLabel,
        // The Arg is the IconKind the filter button toggles.
        ToggleKind,
        Shape, Readable, AnyOf, PickIcon, AddRequirement, DeleteQuest, SaveQuest, RevertQuest, SwapLink, DeleteLink,
        DeleteSelected,
        // The Arg of these is the requirement index.
        CountDown, CountUp, Pick, Remove,
    }

    private const float Pad = DevStudioUi.Pad;
    private const float PanelWidth = DevStudioUi.PanelWidth;
    private const float ButtonH = 28f;
    private const float Gap = 6f;
    private const float FieldH = 28f;
    private const float LabelH = 18f;
    private const float TopBarH = DevStudioUi.ButtonH;
    private const double DoubleClickSeconds = 0.35;
    // How near a line a click has to land to select that link, in canvas units
    private const float LinkTolerance = 6f;
    private static readonly Rgba ErrorColor = new(230, 110, 110);

    private readonly TextFieldState _field = new(400);
    private readonly Dictionary<(Field, int), TextFieldState> _shown = new();
    private readonly List<(UiRect Rect, Action Action, int Arg, string Label, bool Enabled, bool Active)> _buttons = [];
    private readonly List<(UiRect Rect, Field Field, int Arg, string Placeholder)> _fields = [];
    private (Field Field, int Arg)? _focus;
    // One type dropdown per requirement row of the selected quest, rebuilt (closed) when the selection changes
    private static readonly string[] TypeLabels = [.. QuestEditorState.RequirementTypes.Select(t => t.ToUpperInvariant())];
    private readonly List<DropdownState> _typeDropdowns = [];
    private string? _dropdownQuest;
    // When the selected quest shows as a locked preview, in QuestLoader.RevealNames order
    private static readonly string[] RevealLabels =
        ["WHEN UNLOCKED", "ALWAYS", "A PARENT SHOWN", "ALL PARENTS SHOWN", "A PARENT DONE", "A DONE + ALL SHOWN"];
    private readonly DropdownState _revealDropdown = new(RevealLabels);
    // When the selected quest unlocks, in QuestLoader.UnlockNames order
    private static readonly string[] UnlockLabels = ["ALL PARENTS DONE", "ANY PARENT DONE", "ALWAYS"];
    private readonly DropdownState _unlockDropdown = new(UnlockLabels);
    // The selected label's SIZE and COLOR, in QuestLoader.LabelSizeNames / LabelColorNames order
    private readonly DropdownState _labelSize = new([.. QuestLoader.LabelSizeNames.Select(n => n.ToUpperInvariant())]);
    private readonly DropdownState _labelColor = new([.. QuestLoader.LabelColorNames.Select(n => n.ToUpperInvariant())]);
    private string? _dropdownLabel;
    // Every dropdown the last layout placed: its state, the option it shows, and what picking an option does
    private readonly List<(DropdownState Dropdown, int Selected, Action<int> Choose)> _dropdowns = [];
    private string? _focusQuest;
    private string? _focusLabel;

    private enum Drag { None, Pan, Node, Link, Marquee, Label }
    // The label being dragged
    private string? _dragLabel;
    private Drag _drag;
    // A middle-button pan, which never clears the selection the way an unmoved left click on empty canvas does
    private bool _panOnly;
    // The far corner of the selection box being shift-dragged from _pressAt
    private Vector2 _marqueeTo;
    // The multi-selection list: its rows as the last layout placed them, and how far it is scrolled
    private const float ListRowHeight = 34f;
    private ScrollViewLayout? _list;
    private float _listOffset;
    private Vector2? _pointer;
    private Vector2 _pressAt;
    private Vector2 _pressPan;
    private Vector2 _grabbed;
    // Where every selected quest sat when a node drag began, so the whole selection moves with the dragged one
    private readonly Dictionary<string, Vector2> _grabbedMany = new(StringComparer.Ordinal);
    private string? _dragQuest;
    private double _lastClickTime = double.NegativeInfinity;
    // REVERT ALL drops every unsaved edit, so it asks for a second click within this many seconds
    private const double ConfirmSeconds = 3.0;
    private double _revertArmedUntil = double.NegativeInfinity;
    private double _now;
    // Numbers each typing or drag gesture, so a later gesture on the same field or node is a step of its own
    private int _gestureSerial;
    private Vector2 _lastClickAt;
    private Vector2 _screen = new(1600f, 900f);
    // A grid slot pressed in the left panel: a click picks it, a drag onto the canvas adds a quest with it
    private IconEntry? _iconDrag;
    private Vector2 _iconPressAt;
    // The renderer of the last draw, for placing a clicked caret by the drawn text's widths
    private UIRenderer? _ui;

    public QuestEditorState State { get; } = state;

    /// <summary>The icon grid's entries, search and filters; the studio's left panel draws the visible ones.</summary>
    public QuestIconPicker Icons { get; } = icons;

    public QuestViewData Camera { get; } = new();

    /// <summary>The last action's outcome, under the canvas.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Whether a text field has the keyboard (so Space and Delete are typing, not commands).</summary>
    public bool HasFocus => _focus != null;

    /// <summary>The block or item the left grid highlights: the selected quest's icon.</summary>
    public ushort HighlightedIcon => State.SelectedQuestId is { } id && State.Preview.TryGet(id, out var quest) ? quest.IconId : (ushort)0;

    /// <summary>Frames every quest (or the one with <paramref name="id"/>) in the canvas between the side panels.</summary>
    public void Frame(Vector2 screen, string? id = null)
    {
        _screen = screen;
        var quests = State.Preview.Quests.Where(q => id == null || q.Id == id).ToList();
        if (quests.Count == 0) return;
        var min = quests.Aggregate(new Vector2(float.MaxValue), (m, q) => Vector2.Min(m, q.Position));
        var max = quests.Aggregate(new Vector2(float.MinValue), (m, q) => Vector2.Max(m, q.Position));
        var center = (min + max) * 0.5f;
        Camera.PanX = center.X;
        Camera.PanY = center.Y;
        var span = (max - min + new Vector2(2f)) * QuestConfig.GridSpacing;
        var room = new Vector2(CanvasRight - CanvasLeft, screen.Y - CanvasTop - Pad * 6f);
        Camera.Zoom = Math.Clamp(MathF.Min(room.X / span.X, room.Y / span.Y), QuestConfig.MinZoom, 1.4f);
    }

    // The canvas strip between the two side panels, under the top bar
    private float CanvasLeft => Pad * 2f + PanelWidth;
    private float CanvasRight => _screen.X - Pad * 2f - PanelWidth;
    private static float CanvasTop => Pad + TopBarH + Gap;
    private float PanelLeft => _screen.X - Pad - PanelWidth + Pad;
    private float PanelRight => _screen.X - Pad * 2f;

    private bool OverCanvas(Vector2 point) => point.X > CanvasLeft && point.X < CanvasRight && point.Y > CanvasTop;

    // ---- drawing

    /// <summary>Draws the quests over the whole window; the studio's panels go over it.</summary>
    public void DrawCanvas(UIRenderer ui, Vector2? pointer, float time)
    {
        _screen = ui.ScreenSize;
        var book = State.Preview;
        var hovered = pointer is { } p && _drag is Drag.None or Drag.Link && OverCanvas(p)
            ? QuestBookLayout.NodeAt(book.Quests, _ => true, Camera, _screen, p)
            : null;
        ui.Quests.DrawGraph(book, Camera, _ => true, _ => false, q => State.IsSelected(q.Id), hovered, time);

        if (_drag == Drag.Marquee)
        {
            var box = MarqueeBox();
            ui.DrawRect(box.Center, box.Size, DevStudioUi.Accent.WithAlpha((byte)40));
            ui.DrawBorder(box.Center, box.Size, 1.5f, DevStudioUi.Accent);
        }

        // The link being right-dragged: a gold line from its parent to the pointer
        if (_drag == Drag.Link && _dragQuest != null && book.TryGet(_dragQuest, out var from) && pointer is { } tip)
            ui.Quests.DrawLine(QuestBookLayout.ToCanvas(from.Position, Camera, _screen), tip,
                QuestConfig.EdgeThickness * Camera.Zoom, DevStudioUi.Accent);

        // A gold corner mark on every quest with unsaved edits
        var mark = QuestBookLayout.NodeSize(Camera) * 0.16f;
        foreach (var quest in book.Quests)
        {
            if (!State.IsQuestDirty(quest.Id)) continue;
            var corner = QuestBookLayout.ToCanvas(quest.Position, Camera, _screen)
                         - new Vector2(QuestBookLayout.NodeSize(Camera) * 0.36f);
            ui.DrawRect(corner, new Vector2(mark), DevStudioUi.Accent);
        }

        // The selected link, drawn again in gold over the graph, between the node rims so it never covers an icon
        if (State.SelectedLink is { } link && book.TryGet(link.Child, out var child) && book.TryGet(link.Parent, out var parent))
        {
            var start = QuestBookLayout.ToCanvas(parent.Position, Camera, _screen);
            var end = QuestBookLayout.ToCanvas(child.Position, Camera, _screen);
            var rim = Vector2.Normalize(end - start) * QuestBookLayout.NodeSize(Camera) * 0.5f;
            if (Vector2.Distance(start, end) > rim.Length() * 2f)
                ui.Quests.DrawLine(start + rim, end - rim, QuestConfig.EdgeThickness * Camera.Zoom * 1.5f, DevStudioUi.Accent);
        }

        // The selected label, outlined in gold
        if (State.SelectedLabel is { } name && book.Labels.FirstOrDefault(l => l.Id == name) is { } selectedLabel)
        {
            var bounds = ui.Quests.LabelBounds(selectedLabel, Camera, _screen);
            ui.DrawBorder(bounds.Center, bounds.Size, 1.5f, DevStudioUi.Accent);
        }

        if (State.ErrorQuestId is { } bad && book.TryGet(bad, out var broken))
        {
            var size = QuestBookLayout.NodeSize(Camera) * 1.3f;
            ui.DrawBorder(QuestBookLayout.ToCanvas(broken.Position, Camera, _screen), new Vector2(size), 3f, ErrorColor);
        }
    }

    /// <summary>Builds and draws the top bar, the right panel and the status lines.</summary>
    public void DrawPanels(UIRenderer ui, Vector2? pointer, float time)
    {
        _screen = ui.ScreenSize;
        _ui = ui;
        _now = time;
        _buttons.Clear();
        _fields.Clear();
        _dropdowns.Clear();
        BuildTopBar();
        BuildIconHeader();
        BuildDetails();

        // An opaque strip under the top bar, so nodes panned under it do not show through its fields
        ui.DrawRect(new Vector2((CanvasLeft + CanvasRight) * 0.5f, Pad + TopBarH * 0.5f),
            new Vector2(CanvasRight - CanvasLeft + Pad * 2f, TopBarH + Pad), Rgba.Gray(0.05f, 0.92f));
        foreach (var (rect, field, arg, placeholder) in _fields)
        {
            var focused = _focus == (field, arg);
            ui.DrawInputField(rect.Center, rect.Size, focused ? _field : Shown(field, arg), focused, placeholder, time);
        }
        foreach (var (rect, _, _, label, enabled, active) in _buttons)
            DevStudioUi.DrawButton(ui, rect.Center, rect.Size, label, enabled, active);
        _pointer = pointer;
        DrawDetailsText(ui);
        DrawStatus(ui);
        DrawDropdowns(ui, pointer);
        DrawDraggedIcon(ui, pointer, time);
    }

    // The slot being dragged rides the pointer once it has left the click radius
    private void DrawDraggedIcon(UIRenderer ui, Vector2? pointer, float time)
    {
        if (_iconDrag is not { } entry || pointer is not { } p || !IconDragging(p)) return;
        if (entry.Kind == IconKind.Entity)
        {
            ui.DrawTextMiddle(entry.Name, p, ui.CapHeightScale(DevStudioUi.TextCap), Rgba.White);
            return;
        }
        ui.ClearDepthForOverlay();
        ui.DrawIdIcon(p, QuestConfig.NodeIconSize, entry.Id, 0f, time);
    }

    private bool IconDragging(Vector2 point) => Vector2.Distance(point, _iconPressAt) >= QuestConfig.DragThreshold;

    // The closed boxes first, then the open list over everything it covers
    private void DrawDropdowns(UIRenderer ui, Vector2? pointer)
    {
        var scale = ui.CapHeightScale(DevStudioUi.TextCap);
        foreach (var open in new[] { false, true })
            foreach (var (dropdown, selected, _) in _dropdowns)
                if (dropdown.IsOpen == open)
                    DropdownRenderer.Draw(ui, dropdown, selected, pointer, scale);
    }

    private void BuildTopBar()
    {
        var y = Pad + TopBarH * 0.5f;
        var x = CanvasLeft;
        const float wide = 100f;
        const float narrow = 64f;
        x = AddButtonAt(x, y, wide, Action.AddQuest, "ADD QUEST", true);
        x = AddButtonAt(x, y, wide, Action.AddLabel, "ADD LABEL", true);
        x = AddButtonAt(x + Gap * 2f, y, narrow, Action.Undo, "UNDO", State.CanUndo);
        x = AddButtonAt(x, y, narrow, Action.Redo, "REDO", State.CanRedo);
        x = AddButtonAt(x + Gap * 2f, y, wide, Action.Save, State.Dirty ? "SAVE ALL *" : "SAVE ALL", State.Dirty);
        AddButtonAt(x, y, wide, Action.RevertAll, _now < _revertArmedUntil ? "CONFIRM?" : "REVERT ALL", State.Dirty);
    }

    // The icon grid's header in the left panel: the search field, then one toggle per kind
    private void BuildIconHeader()
    {
        var left = Pad * 2f;
        var width = PanelWidth - Pad * 2f;
        var top = Pad + DevStudioUi.TabRowH + 6f;
        _fields.Add((Rect(left, top, width, FieldH), Field.IconSearch, 0, $"search {Icons.Visible.Count} icons"));
        var third = (width - Gap * 2f) / 3f;
        string[] labels = ["ITEMS", "BLOCKS", "ENTITIES"];
        for (var k = 0; k < labels.Length; k++)
            AddButton(Rect(left + (third + Gap) * k, top + FieldH + Gap, third, ButtonH), Action.ToggleKind, labels[k], true,
                Icons.Shows((IconKind)k), k);
    }

    private void BuildDetails()
    {
        if (State.SelectedQuestId != _dropdownQuest)
        {
            _typeDropdowns.Clear();
            _revealDropdown.Close();
            _unlockDropdown.Close();
            _dropdownQuest = State.SelectedQuestId;
        }
        _list = null;
        if (State.SelectedLink != null)
        {
            BuildLinkPanel();
            return;
        }
        if (State.SelectedLabel is { } label)
        {
            BuildLabelPanel(label);
            return;
        }
        if (State.SelectedQuestIds.Count > 1)
        {
            BuildSelectionList();
            return;
        }
        if (State.SelectedQuest is not { } quest) return;
        var left = PanelLeft;
        var width = PanelRight - left;
        var y = Pad + 40f;

        y = AddField(Field.Id, 0, "id", left, y, width);
        y = AddField(Field.Title, 0, "title", left, y, width);
        y = AddField(Field.Description, 0, "description", left, y, width);

        var third = (width - Gap * 2f) / 3f;
        var shape = quest.Shape?.ToUpperInvariant() ?? "AUTO";
        AddButton(Rect(left, y, third, ButtonH), Action.Shape, "SHAPE: " + shape, true, quest.Shape != null);
        AddButton(Rect(left + third + Gap, y, third, ButtonH), Action.Readable,
            quest.Readable == false ? "HIDDEN REQS" : "READABLE", true, quest.Readable == false);
        AddButton(Rect(left + (third + Gap) * 2f, y, third, ButtonH), Action.AnyOf,
            quest.AnyOf == true ? "ANY OF" : "ALL OF", true, quest.AnyOf == true);
        y += ButtonH + Gap;

        // SHOW: when the quest appears as a locked preview (its label is drawn in DrawDetailsText)
        var questId = quest.Id!;
        const float showLabel = 86f;
        _revealDropdown.Box = Rect(left + showLabel, y, width - showLabel, FieldH);
        _dropdowns.Add((_revealDropdown, QuestEditorState.RevealOf(quest), mode => State.SetReveal(questId, mode)));
        y += FieldH + Gap;

        // UNLOCK: when the quest shows its details and can be earned
        _unlockDropdown.Box = Rect(left + showLabel, y, width - showLabel, FieldH);
        _dropdowns.Add((_unlockDropdown, QuestEditorState.UnlockOf(quest), mode => State.SetUnlock(questId, mode)));
        y += FieldH + Gap + LabelH;

        // The icon row: the icon is drawn in DrawDetailsText, the button arms the grid for it
        AddButton(Rect(PanelRight - 110f, y, 110f, ButtonH), Action.PickIcon, "PICK ICON", true, State.PickRequirement < 0);
        y += ButtonH + Gap + LabelH;

        var requirements = quest.Requirements ?? [];
        if (_typeDropdowns.Count > requirements.Count) _typeDropdowns.RemoveRange(requirements.Count, _typeDropdowns.Count - requirements.Count);
        while (_typeDropdowns.Count < requirements.Count) _typeDropdowns.Add(new DropdownState(TypeLabels));
        const float typeWidth = 110f;
        for (var i = 0; i < requirements.Count; i++)
        {
            var requirement = requirements[i];
            _typeDropdowns[i].Box = Rect(left, y, typeWidth, FieldH);
            var row = i;
            _dropdowns.Add((_typeDropdowns[i], Array.IndexOf(QuestEditorState.RequirementTypes, requirement.Type?.ToLowerInvariant()),
                type => State.SetRequirementType(questId, row, QuestEditorState.RequirementTypes[type])));
            // A row near the bottom opens its list upward, so it stays on screen
            _typeDropdowns[i].OpensUp = y + FieldH * (TypeLabels.Length + 1) > _screen.Y - Pad;
            _fields.Add((Rect(left + typeWidth + Gap, y, width - typeWidth - Gap, FieldH), Field.Target, i,
                requirement.Type == "kill" ? "entity" : QuestEditorState.TakesItem(requirement) ? "item or #tag" : "block"));
            y += FieldH + Gap;

            var count = requirement.Count ?? 1;
            AddButton(Rect(left, y, 30f, ButtonH), Action.CountDown, "-", count > 1, false, i);
            AddButton(Rect(left + 30f + 60f, y, 30f, ButtonH), Action.CountUp, "+", true, false, i);
            var pickLabel = requirement.Type == "kill" ? "NEXT MOB" : "PICK";
            AddButton(Rect(left + 130f, y, 90f, ButtonH), Action.Pick, pickLabel, true, State.PickRequirement == i, i);
            AddButton(Rect(PanelRight - 60f, y, 60f, ButtonH), Action.Remove, "X", requirements.Count > 1, false, i);
            y += ButtonH + Gap;

            if (requirement.Tag != null)
            {
                _fields.Add((Rect(left, y, width, FieldH), Field.Label, i, "label (default: Any <tag>)"));
                y += FieldH + Gap;
            }
            y += 4f;
        }
        AddButton(Rect(left, y, width, ButtonH), Action.AddRequirement, "+ REQUIREMENT", true, false);

        var bottom = _screen.Y - Pad * 2f - ButtonH;
        var dirty = State.IsQuestDirty(quest.Id!);
        var half = (width - Gap) * 0.5f;
        AddButton(Rect(left, bottom - ButtonH - Gap, half, ButtonH), Action.SaveQuest, "SAVE QUEST", dirty, false);
        AddButton(Rect(left + half + Gap, bottom - ButtonH - Gap, half, ButtonH), Action.RevertQuest, "REVERT QUEST", dirty, false);
        AddButton(Rect(left, bottom, width, ButtonH), Action.DeleteQuest, "DELETE QUEST", true, false);
    }

    // Several selected quests: a scrolled list from under the heading down to the DELETE button
    private void BuildSelectionList()
    {
        var width = PanelRight - PanelLeft;
        var bottom = _screen.Y - Pad * 2f - ButtonH;
        var view = UiRect.FromTopLeft(new Vector2(PanelLeft, Pad + 44f), new Vector2(width, bottom - Gap - (Pad + 44f)));
        var list = new ScrollViewLayout(view, ListRowHeight, State.SelectedQuestIds.Count);
        _listOffset = list.Clamp(_listOffset);
        _list = list;
        AddButton(Rect(PanelLeft, bottom, width, ButtonH), Action.DeleteSelected,
            $"DELETE {State.SelectedQuestIds.Count} QUESTS  [DEL]", true, false);
    }

    // Every row that sits whole inside the viewport: its icon, its title and, under the pointer, a lit background;
    // a scrollbar along the right edge when the list is longer than the view
    private void DrawSelectionList(UIRenderer ui)
    {
        if (_list is not { } list) return;
        var ids = State.SelectedQuestIds;
        var heading = $"{ids.Count} quests selected";
        ui.DrawText(heading, new Vector2(PanelLeft, Pad + 8f), DevStudioUi.FitText(ui, heading, 18f, list.View.Size.X).Scale,
            Rgba.White);
        ui.ClearDepthForOverlay();
        var scrollbar = list.Overflows ? 6f : 0f;
        var (first, end) = list.VisibleRows(_listOffset);
        for (var row = first; row < end && row < ids.Count; row++)
        {
            var rect = list.RowRect(row, _listOffset);
            if (rect.Top < list.View.Top - 0.5f || rect.Bottom > list.View.Bottom + 0.5f) continue;
            var inner = rect with { Size = rect.Size - new Vector2(scrollbar + 4f, 4f) };
            inner = inner with { Center = new Vector2(rect.Left + inner.Size.X * 0.5f, rect.Center.Y) };
            var hovered = _pointer is { } p && inner.Contains(p);
            ui.DrawRect(inner.Center, inner.Size, hovered ? Rgba.Gray(0.24f, 0.9f) : Rgba.Gray(0.1f, 0.85f));
            if (!State.Preview.TryGet(ids[row], out var quest)) continue;
            ui.DrawIdIcon(new Vector2(inner.Left + 16f, inner.Center.Y), 24f, quest.IconId, 0f, 0f);
            var title = quest.Title == quest.Id ? quest.Id : $"{quest.Title} ({quest.Id})";
            // One smaller size for every row, shrunk further only for a title that still does not fit
            const float rowCap = 10f;
            ui.DrawText(title, new Vector2(inner.Left + 34f, inner.Center.Y - rowCap * 0.5f),
                DevStudioUi.FitText(ui, title, rowCap, inner.Size.X - 40f).Scale, Rgba.White);
        }
        if (!list.Overflows) return;
        var track = UiRect.FromTopLeft(new Vector2(list.View.Right - scrollbar, list.View.Top), new Vector2(scrollbar, list.View.Size.Y));
        ui.DrawRect(track.Center, track.Size, Rgba.Gray(0.12f, 0.9f));
        var thumbHeight = track.Size.Y * list.View.Size.Y / list.ContentHeight;
        var travel = list.ContentHeight - list.View.Size.Y;
        var thumbTop = track.Top + (track.Size.Y - thumbHeight) * (travel > 0f ? _listOffset / travel : 0f);
        ui.DrawRect(new Vector2(track.Center.X, thumbTop + thumbHeight * 0.5f), new Vector2(scrollbar, thumbHeight),
            DevStudioUi.Accent);
    }

    // A selected label: its TEXT field, SIZE and COLOR, and SAVE / REVERT / DELETE LABEL at the bottom
    private void BuildLabelPanel(string name)
    {
        if (State.FindLabel(name) is not { } document) return;
        if (name != _dropdownLabel)
        {
            _labelSize.Close();
            _labelColor.Close();
            _dropdownLabel = name;
        }
        var left = PanelLeft;
        var width = PanelRight - left;
        var y = AddField(Field.LabelText, 0, "text (\\n: new line)", left, Pad + 40f, width);
        const float labelWidth = 86f;
        _labelSize.Box = Rect(left + labelWidth, y, width - labelWidth, FieldH);
        _dropdowns.Add((_labelSize, QuestEditorState.SizeOf(document.Label), size => State.SetLabelSize(name, size)));
        y += FieldH + Gap;
        _labelColor.Box = Rect(left + labelWidth, y, width - labelWidth, FieldH);
        _dropdowns.Add((_labelColor, QuestEditorState.ColorOf(document.Label), color => State.SetLabelColor(name, color)));

        var bottom = _screen.Y - Pad * 2f - ButtonH;
        var half = (width - Gap) * 0.5f;
        AddButton(Rect(left, bottom - ButtonH - Gap, half, ButtonH), Action.SaveLabel, "SAVE LABEL", document.Dirty, false);
        AddButton(Rect(left + half + Gap, bottom - ButtonH - Gap, half, ButtonH), Action.RevertLabel, "REVERT LABEL",
            document.Dirty, false);
        AddButton(Rect(left, bottom, width, ButtonH), Action.DeleteLabel, "DELETE LABEL  [DEL]", true, false);
    }

    private void DrawLabelText(UIRenderer ui, string name)
    {
        var scale = ui.CapHeightScale(DevStudioUi.TextCap);
        var heading = "Label " + name + (State.IsLabelDirty(name) ? " *" : "");
        ui.DrawText(heading, new Vector2(PanelLeft, Pad + 8f), DevStudioUi.FitText(ui, heading, 18f, PanelRight - PanelLeft).Scale,
            Rgba.White);
        foreach (var (rect, field, _, placeholder) in _fields)
            if (field == Field.LabelText)
                ui.DrawText("TEXT", new Vector2(rect.Left, rect.Top - LabelH + 2f), scale, DevStudioUi.TextDim);
        ui.DrawText("SIZE", new Vector2(PanelLeft, _labelSize.Box.Center.Y - DevStudioUi.TextCap * 0.5f), scale, DevStudioUi.TextDim);
        ui.DrawText("COLOR", new Vector2(PanelLeft, _labelColor.Box.Center.Y - DevStudioUi.TextCap * 0.5f), scale, DevStudioUi.TextDim);
    }

    // A selected link: which quest depends on which (drawn in DrawLinkText), SWAP and DELETE LINK
    private void BuildLinkPanel()
    {
        var width = PanelRight - PanelLeft;
        AddButton(Rect(PanelLeft, Pad + 150f, width, ButtonH), Action.SwapLink, "SWAP DIRECTION", true, false);
        AddButton(Rect(PanelLeft, Pad + 150f + ButtonH + Gap, width, ButtonH), Action.DeleteLink, "DELETE LINK  [DEL]", true, false);
    }

    private void DrawLinkText(UIRenderer ui, (string Child, string Parent) link)
    {
        var left = PanelLeft;
        var width = PanelRight - left;
        var scale = ui.CapHeightScale(DevStudioUi.TextCap);
        ui.DrawText("Link", new Vector2(left, Pad + 8f), ui.CapHeightScale(18f), Rgba.White);
        var y = Pad + 44f;
        foreach (var (label, id) in new[] { ("QUEST", link.Child), ("DEPENDS ON", link.Parent) })
        {
            ui.DrawText(label, new Vector2(left, y), scale, DevStudioUi.TextDim);
            var title = State.Find(id)?.Quest.Title is { Length: > 0 } t ? $"{t} ({id})" : id;
            ui.DrawText(title, new Vector2(left, y + 18f), DevStudioUi.FitText(ui, title, DevStudioUi.TextCap, width).Scale,
                Rgba.White);
            y += 46f;
        }
    }

    private float AddField(Field field, int arg, string label, float left, float y, float width)
    {
        _fields.Add((Rect(left, y + LabelH, width, FieldH), field, arg, label));
        return y + LabelH + FieldH + Gap;
    }

    private void DrawDetailsText(UIRenderer ui)
    {
        var scale = ui.CapHeightScale(DevStudioUi.TextCap);
        var left = PanelLeft;
        if (State.SelectedLink is { } selectedLink)
        {
            DrawLinkText(ui, selectedLink);
            return;
        }
        if (State.SelectedLabel is { } selectedLabel)
        {
            DrawLabelText(ui, selectedLabel);
            return;
        }
        if (State.SelectedQuestIds.Count > 1)
        {
            DrawSelectionList(ui);
            return;
        }
        if (State.SelectedQuest is not { } quest)
        {
            const string none = "No quest selected";
            ui.DrawText(none, new Vector2(left, Pad + 8f), DevStudioUi.FitText(ui, none, 18f, PanelRight - left).Scale, Rgba.White);
            string[] hints =
            [
                "Click a node to select it; click empty canvas to clear the selection.",
                "Drag a node to move it (Alt: no snap); shift-drag moves every quest linked to it.",
                "Right-drag from a quest to another to make the second depend on the first; drop on empty canvas to add a quest that depends on it.",
                "Ctrl+click a node to add it to the selection; shift-drag on empty canvas selects every quest in the box.",
                "Click a line to select that link: swap its direction or delete it.",
                "Double-click empty canvas, or drag an icon from the grid onto it, to add a quest.",
                "Drag empty canvas or middle-drag anywhere to pan; the wheel zooms.",
                "Delete deletes the selected quest or link; Ctrl+S saves; Ctrl+Z undoes, Ctrl+Y or Ctrl+Shift+Z redoes.",
            ];
            // Smaller than the panel's text, so every hint fits the panel's height
            const float hintCap = 9f;
            var hintScale = ui.CapHeightScale(hintCap);
            var y = Pad + 44f;
            foreach (var hint in hints)
            {
                foreach (var line in Wrap(ui, hint, hintScale, PanelRight - left))
                {
                    ui.DrawText(line, new Vector2(left, y), hintScale, DevStudioUi.TextDim);
                    y += hintCap + 6f;
                }
                y += 6f;
            }
            return;
        }

        // The heading ends in * while the quest has unsaved edits
        var heading = (quest.Title is { Length: > 0 } title ? title : quest.Id ?? "")
                      + (State.IsQuestDirty(quest.Id!) ? " *" : "");
        ui.DrawText(heading, new Vector2(left, Pad + 8f), DevStudioUi.FitText(ui, heading, 18f, PanelRight - left).Scale,
            Rgba.White);
        foreach (var (rect, field, _, placeholder) in _fields)
            if (field is Field.Id or Field.Title or Field.Description)
                ui.DrawText(placeholder.ToUpperInvariant(), new Vector2(rect.Left, rect.Top - LabelH + 2f), scale, DevStudioUi.TextDim);

        // The icon row and the requirements heading sit over the PICK ICON button
        var pick = _buttons.First(b => b.Action == Action.PickIcon).Rect;
        ui.DrawText("ICON", new Vector2(left, pick.Top - LabelH + 2f), scale, DevStudioUi.TextDim);
        if (State.Preview.TryGet(quest.Id!, out var compiled) && compiled.IconId != 0)
            ui.DrawIdIcon(new Vector2(left + 14f, pick.Center.Y), 26f, compiled.IconId, 0f, 0f);
        ui.DrawText(quest.Icon ?? "", new Vector2(left + 34f, pick.Center.Y - DevStudioUi.TextCap * 0.5f),
            DevStudioUi.FitText(ui, quest.Icon ?? "", DevStudioUi.TextCap, pick.Left - left - 40f).Scale, Rgba.White);
        ui.DrawText("REQUIREMENTS", new Vector2(left, pick.Bottom + Gap + 2f), scale, DevStudioUi.TextDim);
        ui.DrawText("SHOW", new Vector2(left, _revealDropdown.Box.Center.Y - DevStudioUi.TextCap * 0.5f), scale,
            DevStudioUi.TextDim);
        ui.DrawText("UNLOCK", new Vector2(left, _unlockDropdown.Box.Center.Y - DevStudioUi.TextCap * 0.5f),
            DevStudioUi.FitText(ui, "UNLOCK", DevStudioUi.TextCap, _unlockDropdown.Box.Left - left - 4f).Scale, DevStudioUi.TextDim);

        foreach (var (rect, action, arg, _, _, _) in _buttons)
        {
            if (action != Action.CountDown) continue;
            var count = (quest.Requirements![arg].Count ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var width = ui.MeasureTextWidth(count, scale);
            ui.DrawText(count, new Vector2(rect.Right + 30f - width * 0.5f, rect.Center.Y - DevStudioUi.TextCap * 0.5f),
                scale, Rgba.White);
        }
    }

    // The error (red) and the last action's outcome under the canvas, wrapped to its width
    private void DrawStatus(UIRenderer ui)
    {
        var scale = ui.CapHeightScale(DevStudioUi.TextCap);
        var width = CanvasRight - CanvasLeft - Pad * 2f;
        var lines = new List<(string Text, Rgba Color)>();
        if (State.Error is { } error)
            lines.AddRange(Wrap(ui, "Does not compile: " + error, scale, width).Select(l => (l, ErrorColor)));
        if (Status.Length > 0) lines.Add((Status, DevStudioUi.Accent));
        if (lines.Count == 0) return;

        var top = _screen.Y - Pad - lines.Count * 18f - 8f;
        ui.DrawRect(new Vector2((CanvasLeft + CanvasRight) * 0.5f, (top + _screen.Y - Pad) * 0.5f),
            new Vector2(CanvasRight - CanvasLeft, _screen.Y - Pad - top), Rgba.Gray(0.03f, 0.85f));
        for (var i = 0; i < lines.Count; i++)
            ui.DrawText(lines[i].Text, new Vector2(CanvasLeft + Pad, top + 4f + i * 18f), scale, lines[i].Color);
    }

    private static IEnumerable<string> Wrap(UIRenderer ui, string text, float scale, float width)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && ui.MeasureTextWidth(next, scale) > width)
            {
                yield return line;
                line = word;
            }
            else line = next;
        }
        if (line.Length > 0) yield return line;
    }

    private TextFieldState Shown(Field field, int arg)
    {
        if (!_shown.TryGetValue((field, arg), out var shown)) _shown[(field, arg)] = shown = new TextFieldState(400);
        var value = ValueOf(field, arg);
        if (shown.Text != value)
        {
            // Unfocused, a long value shows its start, not where a caret would be
            shown.Set(value);
            shown.MoveCaret(CaretMove.Start, false);
            shown.ViewStart = 0;
        }
        return shown;
    }

    private string ValueOf(Field field, int arg)
    {
        if (field == Field.IconSearch) return Icons.Query;
        // A label's line breaks show as \n, so the one-line field can hold them
        if (field == Field.LabelText)
            return State.SelectedLabel is { } name ? (State.FindLabel(name)?.Label.Text ?? "").Replace("\n", "\\n") : "";
        if (State.SelectedQuest is not { } quest) return "";
        return field switch
        {
            Field.Id => quest.Id ?? "",
            Field.Title => quest.Title ?? "",
            Field.Description => quest.Description ?? "",
            Field.Target => Requirement(quest, arg) is { } r ? QuestEditorState.TargetOf(r) : "",
            Field.Label => Requirement(quest, arg)?.Label ?? "",
            _ => "",
        };
    }

    private static QuestRequirementDefinition? Requirement(QuestDefinition quest, int index) =>
        quest.Requirements is { } list && index >= 0 && index < list.Count ? list[index] : null;

    private float AddButtonAt(float x, float y, float width, Action action, string label, bool enabled)
    {
        AddButton(new UiRect(new Vector2(x + width * 0.5f, y), new Vector2(width, ButtonH)), action, label, enabled, false);
        return x + width + Gap;
    }

    private void AddButton(UiRect rect, Action action, string label, bool enabled, bool active, int arg = 0) =>
        _buttons.Add((rect, action, arg, label, enabled, active));

    private static UiRect Rect(float left, float top, float width, float height) =>
        UiRect.FromTopLeft(new Vector2(left, top), new Vector2(width, height));

    // ---- input

    /// <summary>A left press the studio's own widgets did not take. True when the page used it.</summary>
    public bool MouseDown(Vector2 point, double time, bool ctrl, bool shift)
    {
        _now = time;
        if (ClickDropdowns(point)) return true;
        foreach (var (rect, action, arg, _, enabled, _) in _buttons)
        {
            if (!enabled || !rect.Contains(point)) continue;
            Blur();
            Run(action, arg);
            return true;
        }
        if (_list is { } list && list.RowAt(point, _listOffset) is { } row && row < State.SelectedQuestIds.Count)
        {
            Blur();
            State.Select(State.SelectedQuestIds[row]);
            return true;
        }
        foreach (var (rect, field, arg, _) in _fields)
        {
            if (!rect.Contains(point)) continue;
            if (_focus != (field, arg)) Focus(field, arg);
            _field.SetCaret(_ui?.CaretIndexAt(rect.Center, rect.Size, _field, point.X) ?? _field.Text.Length, false);
            return true;
        }
        Blur();
        if (!OverCanvas(point)) return false;

        _pressAt = point;
        var node = QuestBookLayout.NodeAt(State.Preview.Quests, _ => true, Camera, _screen, point);
        if (node == null && shift)
        {
            // A shift-double-click adds a label; a shift-press starts the selection box
            if (time - _lastClickTime < DoubleClickSeconds && Vector2.Distance(point, _lastClickAt) < 8f)
            {
                _lastClickTime = double.NegativeInfinity;
                Status = $"Added {State.AddLabel(QuestBookLayout.ToGrid(point, Camera, _screen))}";
                return true;
            }
            _lastClickTime = time;
            _lastClickAt = point;
            _drag = Drag.Marquee;
            _marqueeTo = point;
            State.Select(null);
            return true;
        }
        if (node == null && LabelAt(point) is { } label)
        {
            State.SelectLabel(label.Id);
            State.Gesture = $"drag {++_gestureSerial}";
            _drag = Drag.Label;
            _dragLabel = label.Id;
            _grabbed = label.Position;
            return true;
        }
        if (node == null
            && QuestBookLayout.LinkAt(State.Preview.Quests, _ => true, Camera, _screen, point, LinkTolerance) is { } hit)
        {
            State.SelectLink(hit.Child.Id, hit.Parent.Id);
            return true;
        }
        if (node == null)
        {
            var doubleClick = time - _lastClickTime < DoubleClickSeconds && Vector2.Distance(point, _lastClickAt) < 8f;
            _lastClickTime = time;
            _lastClickAt = point;
            if (doubleClick)
            {
                State.AddQuest(QuestBookLayout.ToGrid(point, Camera, _screen), Selected()?.Icon);
                Status = $"Added {State.SelectedQuestId}";
                return true;
            }
            StartPan(point, panOnly: false);
            return true;
        }

        if (ctrl)
        {
            State.ToggleSelected(node.Id);
            return true;
        }

        // A press on a quest of a multi-selection keeps the selection, so dragging it moves them all
        if (shift || !State.IsSelected(node.Id)) State.Select(node.Id);
        State.Gesture = $"drag {++_gestureSerial}";
        _drag = Drag.Node;
        _dragQuest = node.Id;
        _grabbed = node.Position;
        _grabbedMany.Clear();
        // A shift-drag takes every quest linked to this one; a plain drag takes the selection
        foreach (var id in shift ? State.ComponentOf(node.Id) : State.SelectedQuestIds)
            if (State.Preview.TryGet(id, out var quest))
                _grabbedMany[id] = quest.Position;
        return true;
    }

    // An open type list takes the click first (a pick, or dismissing it); then a click on a closed box opens it
    private bool ClickDropdowns(Vector2 point)
    {
        foreach (var (dropdown, _, choose) in _dropdowns)
        {
            if (!dropdown.IsOpen) continue;
            if (dropdown.Click(point, out var chosen) == DropdownClick.Chosen) choose(chosen);
            return true;
        }
        foreach (var (dropdown, _, _) in _dropdowns)
        {
            if (dropdown.Click(point, out _) != DropdownClick.Toggled) continue;
            Blur();
            return true;
        }
        return false;
    }

    /// <summary>A right press on a node starts a link from it; <see cref="RightUp"/> ends it.</summary>
    public void RightDown(Vector2 point)
    {
        Blur();
        if (!OverCanvas(point)) return;
        if (QuestBookLayout.NodeAt(State.Preview.Quests, _ => true, Camera, _screen, point) is not { } node) return;
        _pressAt = point;
        _drag = Drag.Link;
        _dragQuest = node.Id;
    }

    /// <summary>Ends a right-drag link: on another node it makes that node depend on the start, on empty canvas it adds a
    /// quest there depending on the start, anywhere else (or with no travel) it does nothing.</summary>
    public void RightUp(Vector2 point)
    {
        var parent = _drag == Drag.Link ? _dragQuest : null;
        _drag = Drag.None;
        _dragQuest = null;
        if (parent == null || !OverCanvas(point) || Vector2.Distance(point, _pressAt) < QuestConfig.DragThreshold) return;

        if (QuestBookLayout.NodeAt(State.Preview.Quests, _ => true, Camera, _screen, point) is { } target)
        {
            Status = State.AddParent(target.Id, parent) ?? $"{target.Id} now depends on {parent}";
            return;
        }
        var icon = State.Find(parent)?.Quest.Icon;
        if (State.AddQuest(QuestBookLayout.ToGrid(point, Camera, _screen), icon, parent: parent) is { } added)
            Status = $"Added {added}, which depends on {parent}";
    }

    // The topmost label whose text is under the point (labels draw in order, so the last drawn wins)
    private QuestLabel? LabelAt(Vector2 point)
    {
        if (_ui == null) return null;
        for (var i = State.Preview.Labels.Count - 1; i >= 0; i--)
        {
            var label = State.Preview.Labels[i];
            if (_ui.Quests.LabelBounds(label, Camera, _screen).Contains(point)) return label;
        }
        return null;
    }

    // The selection box: the rectangle between the shift-press and the pointer, whichever way it was dragged
    private UiRect MarqueeBox()
    {
        var min = Vector2.Min(_pressAt, _marqueeTo);
        return UiRect.FromTopLeft(min, Vector2.Max(_pressAt, _marqueeTo) - min);
    }

    /// <summary>A middle press: pans from anywhere over the canvas, nodes included.</summary>
    public void PanDown(Vector2 point)
    {
        if (OverCanvas(point)) StartPan(point, panOnly: true);
    }

    private void StartPan(Vector2 point, bool panOnly)
    {
        _pressAt = point;
        _drag = Drag.Pan;
        _panOnly = panOnly;
        _pressPan = new Vector2(Camera.PanX, Camera.PanY);
    }

    /// <summary>Follows a held press: pans, draws the selection box, or moves the grabbed quests once past the drag
    /// threshold.</summary>
    public void MouseMove(Vector2 point, bool alt)
    {
        var travel = point - _pressAt;
        switch (_drag)
        {
            case Drag.Marquee:
                _marqueeTo = point;
                var box = MarqueeBox();
                State.SelectMany(State.Preview.Quests
                    .Where(q => box.Contains(QuestBookLayout.ToCanvas(q.Position, Camera, _screen)))
                    .Select(q => q.Id));
                break;
            case Drag.Pan:
                var pan = QuestBookLayout.PannedBy(_pressPan, travel, Camera.Zoom);
                Camera.PanX = pan.X;
                Camera.PanY = pan.Y;
                break;
            case Drag.Label when travel.Length() >= QuestConfig.DragThreshold && _dragLabel != null:
                State.MoveLabel(_dragLabel, _grabbed + GridTravel(travel), !alt);
                break;
            case Drag.Node when travel.Length() >= QuestConfig.DragThreshold && _dragQuest != null:
                State.MoveQuests(_dragQuest, _grabbed + GridTravel(travel), _grabbedMany, !alt);
                break;
        }
    }

    /// <summary>Ends a press. A press on empty canvas that never traveled far enough to pan clears the selection.</summary>
    public void MouseUp(Vector2 point)
    {
        if (_iconDrag is { } icon) DropIcon(icon, point);
        _iconDrag = null;
        var clicked = Vector2.Distance(point, _pressAt) < QuestConfig.DragThreshold;
        if (_drag == Drag.Pan && !_panOnly && clicked)
            State.Select(null);
        // A click (no drag) on one quest of a multi-selection narrows the selection to it
        if (_drag == Drag.Node && clicked && _dragQuest != null && State.SelectedQuestIds.Count > 1)
            State.Select(_dragQuest);
        _drag = Drag.None;
        _dragQuest = null;
        _dragLabel = null;
        if (_focus == null) State.Gesture = null;
    }

    /// <summary>The wheel over the canvas zooms about the pointer; elsewhere it is not the page's.</summary>
    public bool Scroll(Vector2 point, float wheel)
    {
        if (_list is { } list && list.View.Contains(point))
        {
            _listOffset = list.Clamp(_listOffset - MathF.Sign(wheel) * ListRowHeight);
            return true;
        }
        if (!OverCanvas(point)) return false;
        QuestBookLayout.ZoomAbout(Camera, point, _screen, wheel > 0 ? QuestConfig.ZoomStep : 1f / QuestConfig.ZoomStep);
        return true;
    }

    /// <summary>A pressed key. True when the page used it (typing in a field, or one of its commands).</summary>
    public bool KeyDown(Key key, bool ctrl, bool shift)
    {
        if (ctrl && key == Key.S)
        {
            Blur();
            Status = State.Save();
            return true;
        }
        if (ctrl && key is Key.Z or Key.Y)
        {
            Run(key == Key.Y || shift ? Action.Redo : Action.Undo, 0);
            return true;
        }
        if (_focus is { } focus)
        {
            switch (key)
            {
                case Key.Enter or Key.KeypadEnter:
                    Blur();
                    return true;
                case Key.Escape:
                    _focus = null;
                    State.Gesture = null;
                    return true;
            }
            if (keys.TryHandle(_field, key, true, out var changed) && changed) Commit(focus, false);
            return true;
        }
        switch (key)
        {
            case Key.Delete when State.SelectedLabel is { } label:
                State.DeleteLabel(label);
                Status = $"Deleted {label}";
                return true;
            case Key.Delete when State.SelectedLink is { } link:
                Status = State.RemoveLink(link.Child, link.Parent) ?? $"{link.Child} no longer depends on {link.Parent}";
                return true;
            case Key.Delete when State.SelectedQuestIds.Count > 0:
                var deleting = State.SelectedQuestIds.ToList();
                var deleted = State.DeleteQuests(deleting);
                Status = deleted == 1 ? $"Deleted {deleting[0]}" : $"Deleted {deleted} quests";
                return true;
            case Key.Escape:
                if (State.PickRequirement >= 0) State.PickRequirement = -1;
                else State.Select(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A typed character, for the focused field.</summary>
    public bool KeyChar(char c)
    {
        if (_focus is not { } focus) return false;
        if (_field.TryAppend(c)) Commit(focus, false);
        return true;
    }

    /// <summary>A press on a left-grid slot: released in place it picks (<see cref="Pick"/>), dragged onto the canvas
    /// it adds a quest.</summary>
    public void BeginIconDrag(IconEntry entry, Vector2 point)
    {
        Blur();
        _iconDrag = entry;
        _iconPressAt = point;
    }

    private void DropIcon(IconEntry entry, Vector2 point)
    {
        if (!IconDragging(point))
        {
            Pick(entry);
            return;
        }
        if (!OverCanvas(point)) return;
        if (entry.Kind == IconKind.Entity)
        {
            Status = "An entity has no icon: drop it on a kill requirement";
            return;
        }
        if (State.AddQuest(QuestBookLayout.ToGrid(point, Camera, _screen), entry.Key, entry.Name) is { } added)
            Status = $"Added {added} ({entry.Key})";
    }

    /// <summary>A left-grid click: the selected quest's icon, or the armed requirement's target.</summary>
    private void Pick(IconEntry entry)
    {
        Blur();
        if (State.SelectedQuestId is not { } quest) return;
        if (State.PickRequirement >= 0)
        {
            State.SetRequirementTarget(quest, State.PickRequirement, entry.Key);
            Status = $"Requirement {State.PickRequirement + 1} now names {entry.Key}";
            State.PickRequirement = -1;
            return;
        }
        if (entry.Kind == IconKind.Entity)
        {
            Status = "An entity has no icon: pick an item or a block";
            return;
        }
        State.SetIcon(quest, entry.Key);
        Status = $"Icon: {entry.Key}";
    }

    private Vector2 GridTravel(Vector2 travel) => travel / (QuestConfig.GridSpacing * Camera.Zoom);

    private QuestDefinition? Selected() => State.SelectedQuest;

    private void Run(Action action, int arg)
    {
        var id = State.SelectedQuestId;
        switch (action)
        {
            case Action.ToggleKind:
                Icons.Toggle((IconKind)arg);
                break;
            case Action.AddLabel:
                Status = $"Added {State.AddLabel(new Vector2(Camera.PanX, Camera.PanY))}";
                break;
            case Action.SaveLabel when State.SelectedLabel is { } label:
                Status = State.SaveLabel(label);
                break;
            case Action.RevertLabel when State.SelectedLabel is { } label:
                Status = State.RevertLabel(label);
                break;
            case Action.DeleteLabel when State.SelectedLabel is { } label:
                State.DeleteLabel(label);
                Status = $"Deleted {label}";
                break;
            case Action.AddQuest:
                State.AddQuest(new Vector2(Camera.PanX, Camera.PanY), Selected()?.Icon);
                Status = $"Added {State.SelectedQuestId}";
                break;
            case Action.Save:
                Status = State.Save();
                break;
            case Action.Undo:
                Blur();
                Status = State.Undo() ? "Undone" : "Nothing to undo";
                break;
            case Action.Redo:
                Blur();
                Status = State.Redo() ? "Redone" : "Nothing to redo";
                break;
            case Action.RevertAll when _now < _revertArmedUntil:
                _revertArmedUntil = double.NegativeInfinity;
                Status = State.RevertAll();
                break;
            case Action.RevertAll:
                _revertArmedUntil = _now + ConfirmSeconds;
                Status = "Click CONFIRM? to drop every unsaved quest edit";
                break;
            case Action.SaveQuest when id != null:
                Status = State.SaveQuest(id);
                break;
            case Action.RevertQuest when id != null:
                Status = State.RevertQuest(id);
                break;
            case Action.Shape when id != null:
                State.CycleShape(id);
                break;
            case Action.Readable when id != null:
                State.ToggleReadable(id);
                break;
            case Action.AnyOf when id != null:
                State.ToggleAnyOf(id);
                break;
            case Action.PickIcon:
                State.PickRequirement = -1;
                Status = "Click a grid slot to set the icon";
                break;
            case Action.AddRequirement when id != null:
                State.AddRequirement(id);
                break;
            case Action.SwapLink when State.SelectedLink is { } link:
                Status = State.SwapLink(link.Child, link.Parent) ?? $"{link.Parent} now depends on {link.Child}";
                break;
            case Action.DeleteLink when State.SelectedLink is { } link:
                Status = State.RemoveLink(link.Child, link.Parent) ?? $"{link.Child} no longer depends on {link.Parent}";
                break;
            case Action.DeleteSelected:
                var deleting = State.SelectedQuestIds.ToList();
                Status = $"Deleted {State.DeleteQuests(deleting)} quests";
                break;
            case Action.DeleteQuest when id != null:
                State.DeleteQuest(id);
                Status = $"Deleted {id}";
                break;
            case Action.CountDown when id != null:
                State.AdjustRequirementCount(id, arg, -1);
                break;
            case Action.CountUp when id != null:
                State.AdjustRequirementCount(id, arg, 1);
                break;
            case Action.Pick when id != null:
                PickFor(id, arg);
                break;
            case Action.Remove when id != null:
                State.RemoveRequirement(id, arg);
                break;
        }
    }

    // A kill requirement steps to the next registered entity; any other arms the grid for its target
    private void PickFor(string id, int index)
    {
        if (State.SelectedQuest is not { } quest || Requirement(quest, index) is not { } requirement) return;
        if (requirement.Type != "kill")
        {
            State.PickRequirement = State.PickRequirement == index ? -1 : index;
            Status = State.PickRequirement >= 0 ? "Click a grid slot to set the requirement's target" : "";
            return;
        }
        var names = EntityRegistry.ByName.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (names.Count == 0) return;
        var next = names[(names.IndexOf(requirement.Entity ?? "") + 1) % names.Count];
        State.SetRequirementTarget(id, index, next);
    }

    private void Focus(Field field, int arg)
    {
        Blur();
        _focus = (field, arg);
        _focusQuest = State.SelectedQuestId;
        _focusLabel = State.SelectedLabel;
        State.Gesture = $"field {++_gestureSerial}";
        _field.Set(ValueOf(field, arg));
        _field.ViewStart = 0;
    }

    // Leaves the focused field, committing an id rename (every other field commits as it is typed)
    private void Blur()
    {
        if (_focus is not { } focus) return;
        Commit(focus, true);
        _focus = null;
        State.Gesture = null;
    }

    private void Commit((Field Field, int Arg) focus, bool final)
    {
        var text = _field.Text;
        if (focus.Field == Field.IconSearch)
        {
            Icons.SetQuery(text);
            return;
        }
        if (focus.Field == Field.LabelText)
        {
            if (_focusLabel is { } label) State.SetLabelText(label, text.Replace("\\n", "\n"));
            return;
        }
        if (_focusQuest is not { } id || State.Find(id) == null) return;
        switch (focus.Field)
        {
            case Field.Id when final:
                var refused = State.RenameQuest(id, text);
                Status = refused ?? (text.Trim() == id ? Status : $"Renamed {id} to {text.Trim()}");
                break;
            case Field.Title:
                State.SetTitle(id, text);
                break;
            case Field.Description:
                State.SetDescription(id, text);
                break;
            case Field.Target:
                State.SetRequirementTarget(id, focus.Arg, text);
                break;
            case Field.Label:
                State.SetRequirementLabel(id, focus.Arg, text);
                break;
        }
    }
}
