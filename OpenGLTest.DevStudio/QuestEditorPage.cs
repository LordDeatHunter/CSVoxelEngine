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
using VoxelEngine.Items;
using VoxelEngine.Rendering.Overlays;
using VoxelEngine.Saves;
using VoxelEngine.UI.Menus;
using VoxelEngine.UI.Quests;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.DevStudio;

/// <summary>
/// DevStudio's QUESTS tab over a <see cref="QuestEditorState"/>: the quest graphs drawn exactly as the in-game book
/// draws them (<see cref="QuestBookRenderer.DrawGraph"/>) on the canvas between the side panels, a top bar (graph
/// selector, graph title, NEW GRAPH, ADD QUEST, SAVE), and the selected quest's fields and requirements in the right
/// panel. Clicking a node selects it, dragging moves it (shift-drag moves its whole graph, Alt turns snapping off),
/// Ctrl+click toggles the clicked node as a parent of the selected one, a double-click on empty canvas adds a quest
/// there, dragging empty canvas pans and the wheel zooms. A grid click picks the quest's icon, or the target of a
/// requirement whose PICK is armed; dragging a grid slot onto the canvas adds a quest there with that icon, named
/// after it. A requirement's type is a <see cref="DropdownState"/>. Geometry is rebuilt each draw and hit-tested on the next mouse-down.
/// </summary>
internal sealed class QuestEditorPage(QuestEditorState state, TextFieldKeys keys)
{
    private enum Field { GraphTitle, Id, Title, Description, Target, Label }

    private enum Action
    {
        PrevGraph, NextGraph, NewGraph, AddQuest, Save,
        Shape, Readable, AnyOf, PickIcon, AddRequirement, DeleteQuest,
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
    private string? _focusQuest;

    private enum Drag { None, Pan, Node, Graph }
    private Drag _drag;
    private Vector2 _pressAt;
    private Vector2 _pressPan;
    private Vector2 _grabbed;
    private string? _dragQuest;
    private QuestGraphDocument? _dragGraph;
    private double _lastClickTime = double.NegativeInfinity;
    private Vector2 _lastClickAt;
    private Vector2 _screen = new(1600f, 900f);
    // A grid slot pressed in the left panel: a click picks it, a drag onto the canvas adds a quest with it
    private ushort? _iconDrag;
    private Vector2 _iconPressAt;
    // The renderer of the last draw, for placing a clicked caret by the drawn text's widths
    private UIRenderer? _ui;

    public QuestEditorState State { get; } = state;

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

    /// <summary>Draws the graphs over the whole window; the studio's panels go over it.</summary>
    public void DrawCanvas(UIRenderer ui, Vector2? pointer, float time)
    {
        _screen = ui.ScreenSize;
        var book = State.Preview;
        var hovered = pointer is { } p && _drag == Drag.None && OverCanvas(p)
            ? QuestBookLayout.NodeAt(book.Quests, _ => true, Camera, _screen, p)
            : null;
        QuestType? selected = State.SelectedQuestId is { } id && book.TryGet(id, out var q) ? q : null;
        ui.Quests.DrawGraph(book, Camera, _ => true, _ => false, selected, hovered, time);

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
        _buttons.Clear();
        _fields.Clear();
        BuildTopBar();
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
        DrawDetailsText(ui);
        DrawStatus(ui);
        DrawTypeDropdowns(ui, pointer);
        DrawDraggedIcon(ui, pointer, time);
    }

    // The slot being dragged rides the pointer once it has left the click radius
    private void DrawDraggedIcon(UIRenderer ui, Vector2? pointer, float time)
    {
        if (_iconDrag is not { } id || pointer is not { } p || !IconDragging(p)) return;
        ui.ClearDepthForOverlay();
        ui.DrawIdIcon(p, QuestConfig.NodeIconSize, id, 0f, time);
    }

    private bool IconDragging(Vector2 point) => Vector2.Distance(point, _iconPressAt) >= QuestConfig.DragThreshold;

    // The closed boxes first, then the open list over everything it covers
    private void DrawTypeDropdowns(UIRenderer ui, Vector2? pointer)
    {
        if (State.SelectedQuest?.Requirements is not { } requirements) return;
        var scale = ui.CapHeightScale(DevStudioUi.TextCap);
        foreach (var open in new[] { false, true })
            for (var i = 0; i < _typeDropdowns.Count && i < requirements.Count; i++)
            {
                if (_typeDropdowns[i].IsOpen != open) continue;
                var type = Array.IndexOf(QuestEditorState.RequirementTypes, requirements[i].Type?.ToLowerInvariant());
                DropdownRenderer.Draw(ui, _typeDropdowns[i], type, pointer, scale);
            }
    }

    private void BuildTopBar()
    {
        var y = Pad + TopBarH * 0.5f;
        var x = CanvasLeft;
        var hasGraph = State.Graph != null;
        x = AddButtonAt(x, y, 34f, Action.PrevGraph, "<", State.SelectedGraph > 0);
        _fields.Add((new UiRect(new Vector2(x + 110f, y), new Vector2(220f, FieldH)), Field.GraphTitle, 0, "graph title"));
        x += 220f + Gap;
        x = AddButtonAt(x, y, 34f, Action.NextGraph, ">", State.SelectedGraph < State.Documents.Count - 1);
        x = AddButtonAt(x + Gap * 2f, y, 110f, Action.NewGraph, "NEW GRAPH", true);
        x = AddButtonAt(x, y, 110f, Action.AddQuest, "ADD QUEST", hasGraph);
        AddButtonAt(x, y, 110f, Action.Save, State.Dirty ? "SAVE *" : "SAVE", State.Dirty);
    }

    private void BuildDetails()
    {
        if (State.SelectedQuestId != _dropdownQuest)
        {
            _typeDropdowns.Clear();
            _dropdownQuest = State.SelectedQuestId;
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
        y += ButtonH + Gap + LabelH;

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

        AddButton(Rect(left, _screen.Y - Pad * 2f - ButtonH, width, ButtonH), Action.DeleteQuest, "DELETE QUEST", true, false);
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
        if (State.SelectedQuest is not { } quest)
        {
            const string none = "No quest selected";
            ui.DrawText(none, new Vector2(left, Pad + 8f), DevStudioUi.FitText(ui, none, 18f, PanelRight - left).Scale, Rgba.White);
            string[] hints =
            [
                "Click a node to select it; click empty canvas to clear the selection.",
                "Drag a node to move it (Alt: no snap); shift-drag moves its graph.",
                "Ctrl+click a node to toggle it as a parent of the selected quest.",
                "Double-click empty canvas, or drag an icon from the grid onto it, to add a quest.",
                "Drag empty canvas to pan; the wheel zooms.",
                "Delete deletes the selected quest; Ctrl+S saves.",
            ];
            var y = Pad + 44f;
            foreach (var hint in hints)
            {
                foreach (var line in Wrap(ui, hint, scale, PanelRight - left))
                {
                    ui.DrawText(line, new Vector2(left, y), scale, DevStudioUi.TextDim);
                    y += 18f;
                }
                y += 6f;
            }
            return;
        }

        ui.DrawText(quest.Title is { Length: > 0 } title ? title : quest.Id ?? "", new Vector2(left, Pad + 8f),
            DevStudioUi.FitText(ui, quest.Title ?? quest.Id ?? "", 18f, PanelRight - left).Scale, Rgba.White);
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
        if (field == Field.GraphTitle) return State.Graph?.Definition.Title ?? "";
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

    private static QuestGraphDefinition.RequirementDefinition? Requirement(QuestGraphDefinition.QuestDefinition quest, int index) =>
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
        if (TypeDropdownClick(point)) return true;
        foreach (var (rect, action, arg, _, enabled, _) in _buttons)
        {
            if (!enabled || !rect.Contains(point)) continue;
            Blur();
            Run(action, arg);
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
            _drag = Drag.Pan;
            _pressPan = new Vector2(Camera.PanX, Camera.PanY);
            return true;
        }

        if (ctrl && State.SelectedQuestId is { } child && child != node.Id)
        {
            var wasParent = State.SelectedQuest?.Parents?.Contains(node.Id) == true;
            var refused = State.ToggleParent(child, node.Id);
            Status = refused ?? (wasParent ? $"Unlinked {node.Id} from {child}" : $"{node.Id} is now a parent of {child}");
            return true;
        }

        State.Select(node.Id);
        if (shift && State.Find(node.Id) is { } found)
        {
            _drag = Drag.Graph;
            _dragGraph = found.Document;
            _grabbed = found.Document.Origin;
        }
        else
        {
            _drag = Drag.Node;
            _dragQuest = node.Id;
            _grabbed = node.Position;
        }
        return true;
    }

    // An open type list takes the click first (a pick, or dismissing it); then a click on a closed box opens it
    private bool TypeDropdownClick(Vector2 point)
    {
        if (State.SelectedQuestId is not { } id) return false;
        for (var i = 0; i < _typeDropdowns.Count; i++)
        {
            if (!_typeDropdowns[i].IsOpen) continue;
            if (_typeDropdowns[i].Click(point, out var chosen) == DropdownClick.Chosen)
                State.SetRequirementType(id, i, QuestEditorState.RequirementTypes[chosen]);
            return true;
        }
        foreach (var dropdown in _typeDropdowns)
        {
            if (dropdown.Click(point, out _) != DropdownClick.Toggled) continue;
            Blur();
            return true;
        }
        return false;
    }

    /// <summary>Follows a held press: pans, or moves the grabbed node or graph once past the drag threshold.</summary>
    public void MouseMove(Vector2 point, bool alt)
    {
        var travel = point - _pressAt;
        switch (_drag)
        {
            case Drag.Pan:
                var pan = QuestBookLayout.PannedBy(_pressPan, travel, Camera.Zoom);
                Camera.PanX = pan.X;
                Camera.PanY = pan.Y;
                break;
            case Drag.Node when travel.Length() >= QuestConfig.DragThreshold && _dragQuest != null:
                State.MoveQuest(_dragQuest, _grabbed + GridTravel(travel), !alt);
                break;
            case Drag.Graph when travel.Length() >= QuestConfig.DragThreshold && _dragGraph != null:
                State.MoveGraph(_dragGraph, _grabbed + GridTravel(travel), !alt);
                break;
        }
    }

    /// <summary>Ends a press. A press on empty canvas that never traveled far enough to pan clears the selection.</summary>
    public void MouseUp(Vector2 point)
    {
        if (_iconDrag is { } icon) DropIcon(icon, point);
        _iconDrag = null;
        if (_drag == Drag.Pan && Vector2.Distance(point, _pressAt) < QuestConfig.DragThreshold)
            State.Select(null);
        _drag = Drag.None;
        _dragQuest = null;
        _dragGraph = null;
    }

    /// <summary>The wheel over the canvas zooms about the pointer; elsewhere it is not the page's.</summary>
    public bool Scroll(Vector2 point, float wheel)
    {
        if (!OverCanvas(point)) return false;
        QuestBookLayout.ZoomAbout(Camera, point, _screen, wheel > 0 ? QuestConfig.ZoomStep : 1f / QuestConfig.ZoomStep);
        return true;
    }

    /// <summary>A pressed key. True when the page used it (typing in a field, or one of its commands).</summary>
    public bool KeyDown(Key key, bool ctrl)
    {
        if (ctrl && key == Key.S)
        {
            Blur();
            Status = State.Save();
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
                    return true;
            }
            if (keys.TryHandle(_field, key, true, out var changed) && changed) Commit(focus, false);
            return true;
        }
        switch (key)
        {
            case Key.Delete when State.SelectedQuestId is { } id:
                State.DeleteQuest(id);
                Status = $"Deleted {id}";
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
    public void BeginIconDrag(ushort id, Vector2 point)
    {
        Blur();
        _iconDrag = id;
        _iconPressAt = point;
    }

    private void DropIcon(ushort id, Vector2 point)
    {
        if (!IconDragging(point))
        {
            Pick(id);
            return;
        }
        if (!OverCanvas(point)) return;
        var name = QuestEditorState.ContentName(id);
        if (State.AddQuest(QuestBookLayout.ToGrid(point, Camera, _screen), name, ItemRegistry.TryGet(id)?.Name) is { } added)
            Status = $"Added {added} ({name})";
    }

    /// <summary>A left-grid click: the selected quest's icon, or the armed requirement's target.</summary>
    private void Pick(ushort id)
    {
        Blur();
        if (State.SelectedQuestId is not { } quest) return;
        var name = QuestEditorState.ContentName(id);
        if (State.PickRequirement >= 0)
        {
            State.SetRequirementTarget(quest, State.PickRequirement, name);
            Status = $"Requirement {State.PickRequirement + 1} now names {name}";
            State.PickRequirement = -1;
            return;
        }
        State.SetIcon(quest, name);
        Status = $"Icon: {name}";
    }

    private Vector2 GridTravel(Vector2 travel) => travel / (QuestConfig.GridSpacing * Camera.Zoom);

    private QuestGraphDefinition.QuestDefinition? Selected() => State.SelectedQuest;

    private void Run(Action action, int arg)
    {
        var id = State.SelectedQuestId;
        switch (action)
        {
            case Action.PrevGraph:
                State.SelectedGraph--;
                FrameGraph();
                break;
            case Action.NextGraph:
                State.SelectedGraph++;
                FrameGraph();
                break;
            case Action.NewGraph:
                var document = State.AddGraph(new Vector2(Camera.PanX, Camera.PanY));
                Status = $"New graph {System.IO.Path.GetFileName(document.Path)}";
                break;
            case Action.AddQuest:
                State.AddQuest(new Vector2(Camera.PanX, Camera.PanY), Selected()?.Icon);
                Status = $"Added {State.SelectedQuestId}";
                break;
            case Action.Save:
                Status = State.Save();
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

    private void FrameGraph()
    {
        if (State.Graph is not { } graph) return;
        var ids = graph.Quests.Select(q => q.Id).ToHashSet();
        var quests = State.Preview.Quests.Where(q => ids.Contains(q.Id)).ToList();
        if (quests.Count == 0) return;
        var center = quests.Aggregate(Vector2.Zero, (sum, q) => sum + q.Position) / quests.Count;
        Camera.PanX = center.X;
        Camera.PanY = center.Y;
    }

    private void Focus(Field field, int arg)
    {
        Blur();
        _focus = (field, arg);
        _focusQuest = State.SelectedQuestId;
        _field.Set(ValueOf(field, arg));
        _field.ViewStart = 0;
    }

    // Leaves the focused field, committing an id rename (every other field commits as it is typed)
    private void Blur()
    {
        if (_focus is not { } focus) return;
        Commit(focus, true);
        _focus = null;
    }

    private void Commit((Field Field, int Arg) focus, bool final)
    {
        var text = _field.Text;
        if (focus.Field == Field.GraphTitle)
        {
            if (State.Graph is { } graph) State.SetGraphTitle(graph, text);
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
