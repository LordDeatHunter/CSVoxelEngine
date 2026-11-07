using System;
using VoxelEngine.Config;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The one-line status of an iron works block, shown under the look-at label while the crosshair rests on it: how hot
/// a lit bloomery is (the player keeps it in the band by jumping), what it holds, what waits to be collected, and how
/// far an anvil's piece has been hammered. GL-free, so the HUD and the tests read the same text.
/// </summary>
public static class SmithingReadout
{
    /// <summary>The status line of a bloomery.</summary>
    public static string Bloomery(BloomeryManager.BloomeryState state)
    {
        if (state.Lit)
        {
            var zone = BloomeryRules.ZoneOf(state.Heat) switch
            {
                HeatZone.Cold => "too cold",
                HeatZone.Hot => "too hot",
                _ => "good",
            };
            var progress = state.UnitProgress / BloomeryConfig.SecondsPerOre;
            return $"Heat {(int)MathF.Round(state.Heat * 100f)}% ({zone})   Ore {state.Ore}   Smelted {(int)(progress * 100f)}%";
        }

        if (state.Blooms + state.Slag > 0)
            return $"{state.Blooms} bloom{(state.Blooms == 1 ? "" : "s")}, {state.Slag} slag - empty hand collects";

        var text = $"Ore {state.Ore}/{state.Profile.OreCap}   Fuel {state.Fuel}/{state.Profile.FuelCap}";
        return state.Ore > 0 && state.Fuel > 0 ? text + "   (strike to light)" : text;
    }

    /// <summary>The status line of an anvil, or null when nothing lies on it.</summary>
    public static string? Anvil(AnvilManager.State state) =>
        state.HasPiece ? $"Hammered {state.Hits}/{state.Profile.Hits}" : null;
}
