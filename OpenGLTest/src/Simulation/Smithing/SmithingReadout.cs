using System;
using VoxelEngine.Config;
using VoxelEngine.Content;
using VoxelEngine.Simulation.Leatherwork;
using VoxelEngine.Simulation.WorkStations;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The one-line status of an iron works block, shown under the look-at label while the crosshair rests on it: how hot
/// a lit bloomery is (landings on its bellows keep it at the band), what it holds, what waits to be collected, and
/// what lies on a work station (an anvil, a fleshing beam) and how far its recipe has been worked. GL-free, so the HUD
/// and the tests read the same text.
/// </summary>
public static class SmithingReadout
{
    /// <summary>The status line of a bloomery.</summary>
    public static string Bloomery(BloomeryManager.BloomeryState state)
    {
        if (state.Lit)
        {
            var zone = BloomeryRules.Smelts(state.Heat) ? "good" : "too cold";
            var progress = state.UnitProgress / BloomeryConfig.SecondsPerOre;
            return $"Heat {(int)MathF.Round(state.Heat * 100f)}% ({zone})   Ore {state.Ore}   Smelted {(int)(progress * 100f)}%";
        }

        if (state.Blooms > 0)
            return $"{state.Blooms} bloom{(state.Blooms == 1 ? "" : "s")} - empty hand collects";

        var text = $"Ore {state.Ore}/{state.Profile.OreCap}   Fuel {state.Fuel}/{state.Profile.FuelCap}";
        return state.Ore > 0 && state.Fuel > 0 ? text + "   (strike to light)" : text;
    }

    /// <summary>The status line of a tanning vat: the waiting result, the batch's progress or what it lacks, or what
    /// it holds while no batch is in; null when it holds nothing.</summary>
    public static string? Vat(TanningVatManager.VatState state)
    {
        if (state.Profile is not { } profile || state.IsEmpty) return null;
        if (state.Leather > 0)
            return $"{Inventory.DisplayName(profile.ResultId)} x{state.Leather} - empty hand collects";
        var water = state.Water ? "water" : "no water";
        var stock = $"{Inventory.DisplayName(profile.TanninId)} {state.Bark}   {water}";
        if (state.Hides == 0) return $"{stock}   (add {Inventory.DisplayName(profile.InputId)})";
        var batch = $"{Inventory.DisplayName(profile.InputId)} x{state.Hides}";
        if (TanningRules.CanTan(state))
            return $"Tanning {batch} {(int)(TanningRules.Progress01(state, profile) * 100f)}%";
        var lacking = !state.Water
            ? "needs water"
            : $"needs {Inventory.DisplayName(profile.TanninId)} {state.Bark}/{state.Hides}";
        return $"{batch}   {lacking}";
    }

    /// <summary>The status line of a work station (the recipe being worked and its hits, the stack's input and size,
    /// and a finished result waiting for its collect item), or null when nothing lies on it.</summary>
    public static string? WorkStation(WorkStationManager.State state)
    {
        var waiting = state.FinishedCount > 0
                      && ContentRegistry.WorkRecipeForResult(state.FinishedId) is { } done
            ? $"{Inventory.DisplayName(done.ResultId)} x{state.FinishedCount} (right-click with {Inventory.DisplayName(done.CollectWithId)})"
            : null;
        if (state.InputCount <= 0) return waiting;

        var work = state.Selected is { } recipe
            ? $"Working {Inventory.DisplayName(recipe.ResultId)} {state.Hits}/{recipe.Hits}"
            : state.Working != null
                ? $"{Inventory.DisplayName(state.InputId)} x{state.InputCount}   (strike to work)"
                : $"{Inventory.DisplayName(state.InputId)} x{state.InputCount}   (right-click with the tool to pick)";
        return waiting == null ? work : $"{work}   {waiting}";
    }
}
