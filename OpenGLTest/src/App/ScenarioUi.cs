using System;
using System.Numerics;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.VisualTest;

namespace VoxelEngine.App;

/// <summary>
/// The app's <see cref="IScenarioUi"/>: a visual scenario drives the [E] panel and the spell builder through
/// the same input components a player's keys and clicks reach, so what it films is what the game does.
/// Built by Program from its own components; main thread only.
/// </summary>
internal sealed class ScenarioUi(InventoryPanelInput panelInput, SpellBuilderInput builderInput) : IScenarioUi
{
    public void SetPanelOpen(bool open)
    {
        RequirePlaying("panel");
        panelInput.SetInventoryOpen(open);
    }

    public void SelectTab(string tab)
    {
        RequirePlaying("tab");
        if (!Enum.TryParse<InventoryCategory>(tab, ignoreCase: true, out var category))
            throw new InvalidOperationException($"'{tab}' is not an inventory tab.");
        panelInput.SelectTab(category);
    }

    public void TypeSearch(string text)
    {
        RequirePlaying("search");
        panelInput.TypeSearch(text);
    }

    public void PickRow(int row)
    {
        RequirePlaying("row");
        panelInput.PickRow(row);
    }

    public void SetPointer(Vector2? canvasPoint) => panelInput.ScriptedPointer = canvasPoint;

    public void SetSpellBuilderOpen(bool open)
    {
        var world = RequirePlaying("spellbuilder");
        if (open && !HeldStaff.TryGet(world.Inventory, out _, out _))
            throw new InvalidOperationException("opening the spell builder needs a staff in hand (give one first).");
        builderInput.SetOpen(open);
    }

    private static World RequirePlaying(string what) =>
        Program.ActiveWorld is { IsPlaying: true } world
            ? world
            : throw new InvalidOperationException($"the world is not playing yet; wait before a {what} step.");
}
