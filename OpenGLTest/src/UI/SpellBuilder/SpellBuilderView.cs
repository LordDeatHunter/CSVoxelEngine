using System;
using System.Collections.Generic;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.SpellBuilder;

/// <summary>
/// State of the spell builder panel the spell table opens: whether it is open, the known parts it offers
/// per kind, the recipe being assembled (one element, one shape, any distinct modifiers, at most one
/// trigger) and the staff slot it binds into. GL-free and main thread only; owned by <see cref="Program"/>,
/// changed by the builder input and only read by its renderer. <see cref="Version"/> moves on every
/// selection or slot change, which keys the cached part list, keys and stats, so a steady frame allocates
/// nothing.
/// </summary>
public sealed class SpellBuilderView
{
    private static readonly IReadOnlyList<SpellPart> NoParts = Array.Empty<SpellPart>();

    private readonly IReadOnlyList<SpellPart>[] _columns = [NoParts, NoParts, NoParts, NoParts];
    private readonly List<SpellPart> _modifiers = new();

    // What the columns were last built from; Refresh rebuilds when any of it changed.
    private bool _invalid = true;
    private bool _builtCreative;
    private int _builtKnownVersion;
    private Inventory? _builtInventory;

    private SpellPart[] _parts = [];
    private string[] _keys = [];
    private int _partsVersion = -1;
    private int _keysVersion = -1;
    private SpellStats _stats;
    private int _statsVersion = -1;
    private StaffItem? _statsStaff;

    public bool IsOpen { get; set; }

    /// <summary>The panel's open and close easing, stepped by the UI pass.</summary>
    public PanelTransition Transition { get; } = new();

    /// <summary>The staff slot BIND and CLEAR act on.</summary>
    public int Slot { get; private set; }

    /// <summary>The selected element, or null.</summary>
    public SpellPart? Element { get; private set; }

    /// <summary>The selected shape, or null.</summary>
    public SpellPart? Shape { get; private set; }

    /// <summary>The selected trigger, or null.</summary>
    public SpellPart? Trigger { get; private set; }

    /// <summary>The selected modifiers, distinct, in the order they were picked.</summary>
    public IReadOnlyList<SpellPart> Modifiers => _modifiers;

    /// <summary>Moves on every selection or slot change.</summary>
    public int Version { get; private set; }

    /// <summary>The known parts the column of <paramref name="kind"/> offers, as of the last <see cref="Refresh"/>.</summary>
    public IReadOnlyList<SpellPart> Column(SpellPartKind kind) => _columns[(int)kind];

    /// <summary>
    /// Rebuilds the columns when the view was invalidated (a new view, <see cref="Reset"/>,
    /// <see cref="Invalidate"/>), when <paramref name="creative"/> or <paramref name="inventory"/> differs from
    /// the last rebuild, or when the inventory's <see cref="Inventory.KnownPartsVersion"/> moved; a rebuild also
    /// drops every selected part the player no longer knows. Otherwise it does nothing.
    /// </summary>
    public void Refresh(Inventory inventory, bool creative)
    {
        if (!_invalid && creative == _builtCreative && ReferenceEquals(inventory, _builtInventory)
            && inventory.KnownPartsVersion == _builtKnownVersion)
            return;

        foreach (var kind in SpellBuilderCatalog.Columns)
            _columns[(int)kind] = SpellBuilderCatalog.Known(kind, inventory, creative);
        _invalid = false;
        _builtCreative = creative;
        _builtInventory = inventory;
        _builtKnownVersion = inventory.KnownPartsVersion;

        var dropped = false;
        if (Element != null && !KnownSpellParts.Knows(inventory, Element, creative)) { Element = null; dropped = true; }
        if (Shape != null && !KnownSpellParts.Knows(inventory, Shape, creative)) { Shape = null; dropped = true; }
        if (Trigger != null && !KnownSpellParts.Knows(inventory, Trigger, creative)) { Trigger = null; dropped = true; }
        if (_modifiers.RemoveAll(m => !KnownSpellParts.Knows(inventory, m, creative)) > 0) dropped = true;
        if (dropped) Version++;
    }

    /// <summary>
    /// Picks or drops <paramref name="part"/>: an element, shape or trigger replaces the one of its kind, or is
    /// deselected when it is already the pick; a modifier toggles in or out of the list.
    /// </summary>
    public void Toggle(SpellPart part)
    {
        switch (part.Kind)
        {
            case SpellPartKind.Element:
                Element = ReferenceEquals(Element, part) ? null : part;
                break;
            case SpellPartKind.Shape:
                Shape = ReferenceEquals(Shape, part) ? null : part;
                break;
            case SpellPartKind.Trigger:
                Trigger = ReferenceEquals(Trigger, part) ? null : part;
                break;
            default:
                if (!_modifiers.Remove(part)) _modifiers.Add(part);
                break;
        }
        Version++;
    }

    /// <summary>Whether <paramref name="part"/> is in the current selection.</summary>
    public bool IsSelected(SpellPart part) => part.Kind switch
    {
        SpellPartKind.Element => ReferenceEquals(Element, part),
        SpellPartKind.Shape => ReferenceEquals(Shape, part),
        SpellPartKind.Trigger => ReferenceEquals(Trigger, part),
        _ => _modifiers.Contains(part),
    };

    /// <summary>The selected parts in recipe order: element, shape, modifiers, trigger. Cached per <see cref="Version"/>.</summary>
    public IReadOnlyList<SpellPart> Parts()
    {
        if (_partsVersion == Version) return _parts;

        var parts = new List<SpellPart>(_modifiers.Count + 3);
        if (Element != null) parts.Add(Element);
        if (Shape != null) parts.Add(Shape);
        parts.AddRange(_modifiers);
        if (Trigger != null) parts.Add(Trigger);
        _parts = parts.ToArray();
        _partsVersion = Version;
        return _parts;
    }

    /// <summary>The keys of <see cref="Parts"/>, what a bind sends. Cached per <see cref="Version"/>.</summary>
    public string[] PartKeys()
    {
        if (_keysVersion == Version) return _keys;

        var parts = Parts();
        var keys = new string[parts.Count];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = parts[i].Key;
        _keys = keys;
        _keysVersion = Version;
        return _keys;
    }

    /// <summary>
    /// What the selection compiles to against <paramref name="staff"/>'s tags. Cached on the
    /// <see cref="Version"/> and the staff.
    /// </summary>
    public SpellStats StatsFor(StaffItem staff)
    {
        if (_statsVersion == Version && ReferenceEquals(_statsStaff, staff)) return _stats;

        _stats = new SpellRecipe(Parts()).Compile(staff.AllowedTags);
        _statsVersion = Version;
        _statsStaff = staff;
        return _stats;
    }

    /// <summary>
    /// Points BIND and CLEAR at <paramref name="slot"/> (clamped to the staff's slots). A bound slot loads its
    /// recipe into the selection (keys that no longer resolve are skipped); an unbound one keeps the selection,
    /// so one recipe can be bound into several slots.
    /// </summary>
    public void SelectSlot(int slot, StaffItem staff, in StaffComponent component)
    {
        Slot = Math.Clamp(slot, 0, Math.Max(0, staff.Slots - 1));
        if (component.IsBound(Slot))
        {
            ResetPicks();
            foreach (var key in component.PartsAt(Slot))
            {
                if (SpellPartRegistry.TryGetByKey(key) is not { } part) continue;
                switch (part.Kind)
                {
                    case SpellPartKind.Element: Element = part; break;
                    case SpellPartKind.Shape: Shape = part; break;
                    case SpellPartKind.Trigger: Trigger = part; break;
                    default:
                        if (!_modifiers.Contains(part)) _modifiers.Add(part);
                        break;
                }
            }
        }
        Version++;
    }

    /// <summary>Empties the selection; the slot is kept.</summary>
    public void ClearSelection()
    {
        ResetPicks();
        Version++;
    }

    /// <summary>Makes the next <see cref="Refresh"/> rebuild the columns (the panel opening).</summary>
    public void Invalidate() => _invalid = true;

    /// <summary>Closed, nothing selected, slot 0, and the columns rebuilt on the next refresh.</summary>
    public void Reset()
    {
        IsOpen = false;
        ClearSelection();
        Slot = 0;
        _builtInventory = null;
        Invalidate();
    }

    /// <summary><see cref="Reset"/> plus the transition, when a world is opened or closed.</summary>
    public void ResetForNewWorld()
    {
        Reset();
        Transition.Reset();
    }

    private void ResetPicks()
    {
        Element = null;
        Shape = null;
        Trigger = null;
        _modifiers.Clear();
    }
}
