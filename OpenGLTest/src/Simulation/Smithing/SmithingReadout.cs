using System;
using VoxelEngine.Config;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The one-line status of an iron works block, shown under the look-at label while the crosshair rests on it: how hot
/// a lit bloomery is (landings on its bellows keep it at the band), what it holds, what waits to be collected, and how
/// much metal is stacked on an anvil or how far its picked recipe has been hammered. GL-free, so the HUD and the tests
/// read the same text.
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

    /// <summary>The status line of an anvil (the picked recipe's hits, or the stack's metal and size), or null when
    /// nothing lies on it.</summary>
    public static string? Anvil(AnvilManager.State state)
    {
        if (state.MetalCount <= 0) return null;
        if (state.Selected is { } recipe)
            return $"Forging {Inventory.DisplayName(recipe.ResultId)} {state.Hits}/{recipe.Hits}";
        return $"{Inventory.DisplayName(state.MetalId)} x{state.MetalCount}   (right-click with a hammer to pick)";
    }
}
