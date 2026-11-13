using System;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.VisualTest;

namespace VoxelEngine.App;

/// <summary>
/// The app's <see cref="IScenarioUi"/>: a visual scenario drives the [E] panel, the spell builder, the waystone screen and the
/// anvil's recipe list through
/// the same input components a player's keys and clicks reach, so what it films is what the game does.
/// Built by Program from its own components; main thread only.
/// </summary>
internal sealed class ScenarioUi(InventoryPanelInput panelInput, SpellBuilderInput builderInput, WaystoneInput waystoneInput,
    ForgeInput forgeInput, ChatInput chatInput) : IScenarioUi
{
    public void Chat(string action, string? text)
    {
        RequirePlaying("chat");
        chatInput.Scripted(action, text);
    }

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

    public void OpenChest(int x, int y, int z)
    {
        var world = RequirePlaying("chest");
        panelInput.Chest.Open(world, new Vector3(x, y, z));
        if (!panelInput.Chest.IsOpen)
            throw new InvalidOperationException($"({x}, {y}, {z}) is not a chest in reach.");
    }

    public void OpenLantern()
    {
        var world = RequirePlaying("lantern");
        if (!world.RightClick.TryUseSelected(World.InteractionReach, out var outcome) || outcome != RightClickOutcome.Lantern)
            throw new InvalidOperationException("The click did not open a lantern.");
        panelInput.Chest.OpenLantern(world, world.RightClick.Lantern.Instance, world.RightClick.Lantern.Cell);
    }

    public void CloseChest() => panelInput.Chest.Close();

    public void PickChestRow(int row, bool all)
    {
        var world = RequirePlaying("chestrow");
        var view = world.ChestPanel;
        var rows = view.Rows();
        if (!view.IsOpen || row < 0 || row >= rows.Count)
            throw new InvalidOperationException($"Chest row {row + 1} is not listed ({rows.Count} rows).");
        panelInput.Chest.Withdraw(world, rows[row], all);
    }

    public void Waystone(string action, Vector3? cell, string? text, int row)
    {
        var world = RequirePlaying("waystone");
        switch (action)
        {
            case "open":
                waystoneInput.OpenFromStation(world, cell!.Value);
                if (!waystoneInput.IsOpen)
                    throw new InvalidOperationException($"{cell} is not a waystone in reach.");
                break;
            case "confirm":
                waystoneInput.Confirm();
                break;
            case "rename":
                if (!waystoneInput.BeginRename())
                    throw new InvalidOperationException("RENAME is only offered on the discovery popup to the first discoverer.");
                break;
            case "type":
                waystoneInput.TypeText(text ?? "");
                break;
            case "submit":
                waystoneInput.SubmitRename();
                break;
            case "back":
                waystoneInput.Back();
                break;
            case "close":
                waystoneInput.Close();
                break;
            case "teleport":
                if (!waystoneInput.TeleportToRow(row))
                    throw new InvalidOperationException($"Waystone row {row + 1} is not listed.");
                break;
        }
        // The input's per-frame refresh (rows, footer, reach) runs in GameplayInput, which a scenario bypasses
        waystoneInput.Update(world);
    }

    public void Forge(string action, Vector3? cell, int card)
    {
        var world = RequirePlaying("forge");
        switch (action)
        {
            case "open":
                if (!Mining.IsHammer(world.Inventory.HeldItem))
                    throw new InvalidOperationException("opening the anvil's recipes needs a smithing hammer in hand (give and select one first).");
                forgeInput.Open(world, cell!.Value);
                break;
            case "pick":
                if (!forgeInput.Pick(card))
                    throw new InvalidOperationException($"Forge card {card + 1} is not listed ({forgeInput.View.Recipes.Count} cards).");
                break;
            case "close":
                forgeInput.Close();
                break;
        }
        // The input's per-frame check (the stack, the hammer, the reach) runs in GameplayInput, which a scenario bypasses
        forgeInput.Update(world);
        if (action == "open" && !forgeInput.IsOpen)
            throw new InvalidOperationException($"{cell} is not an anvil with a metal stack in reach.");
    }

    private static World RequirePlaying(string what) =>
        Program.ActiveWorld is { IsPlaying: true } world
            ? world
            : throw new InvalidOperationException($"the world is not playing yet; wait before a {what} step.");
}
