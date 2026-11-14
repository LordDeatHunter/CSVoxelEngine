using System;
using System.Collections.Generic;
using VoxelEngine.Config;
using VoxelEngine.Content;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The progress bars of a station under the crosshair (a lit campfire's fuel and cooking slots, a kiln's fuel and
/// melt, a bloomery's fuel, ore and smelt, an anvil's hammering, a mold's pour and cooling): GL-free, so the HUD and
/// the tests read the same numbers. Each builder returns null when the station shows nothing.
/// </summary>
public static class StationBars
{
    /// <summary>The bars of the branch pile at a cell: while it burns, the fuel left across its branches and one bar per
    /// cooking slot.</summary>
    public static List<StationBar>? Campfire(CampfireManager campfires, int x, int y, int z, byte state, int stackMax)
    {
        if (campfires.BurnLeftAt(x, y, z) is not { } left) return null;
        var max = stackMax >= 2 ? stackMax : 4;
        var branches = BranchPileGenerator.CountWithState(state, max);
        var seconds = (branches - 1) * CampfireManager.BurnSeconds + left;
        var bars = new List<StationBar>
        {
            new($"Fuel {Clock(seconds)}", seconds / (max * CampfireManager.BurnSeconds), StationBarKind.Fuel),
        };
        if (campfires.CookingAt(x, y, z) is not { } slots) return bars;
        foreach (var slot in slots)
        {
            var name = Inventory.DisplayName(slot.IsDone ? slot.OutputId : slot.InputId);
            var progress = slot.TotalTime > 0f ? 1f - Math.Clamp(slot.TimeLeft / slot.TotalTime, 0f, 1f) : 1f;
            bars.Add(slot.IsDone
                ? new StationBar($"{name} ready", 1f, StationBarKind.Done)
                : new StationBar(name, progress, StationBarKind.Progress));
        }
        return bars;
    }

    /// <summary>The bars of a kiln or alloy furnace: fuel, the melt under way and how full its crucible is.</summary>
    public static List<StationBar>? Kiln(KilnManager.KilnState? kiln)
    {
        if (kiln == null) return null;
        var cap = Math.Max(1, kiln.Profile.FuelCap);
        var burning = kiln.FuelSeconds > 0f && kiln.Lit ? Math.Clamp(kiln.BurnLeft / kiln.FuelSeconds, 0f, 1f) : 0f;
        var bars = new List<StationBar>
        {
            new(kiln.Lit ? $"Fuel {Clock(kiln.Fuel * kiln.FuelSeconds + kiln.BurnLeft)}" : $"Fuel {kiln.Fuel}/{cap}",
                (kiln.Fuel + burning) / cap, StationBarKind.Fuel),
        };
        if (!kiln.HasCrucible) return bars;
        if (KilnManager.MeltingMaterial(kiln) != 0)
            bars.Add(new StationBar("Melting", KilnManager.MeltProgress01(kiln), StationBarKind.Progress));
        var capacity = KilnManager.CrucibleCapacityOf(kiln);
        if (capacity > 0 && kiln.Contents.TotalMl > 0)
            bars.Add(new StationBar($"Crucible {(int)MathF.Round(KilnManager.DisplayMoltenMl(kiln))}/{capacity} ml molten",
                KilnManager.DisplayMoltenMl(kiln) / capacity, StationBarKind.Progress));
        return bars;
    }

    /// <summary>The bars of a bloomery: fuel, ore and, while it burns, the ore being smelted.</summary>
    public static List<StationBar>? Bloomery(BloomeryManager.BloomeryState? state)
    {
        if (state == null) return null;
        var profile = state.Profile;
        var bars = new List<StationBar>
        {
            new($"Fuel {state.Fuel}/{profile.FuelCap}", state.Fuel / (float)Math.Max(1, profile.FuelCap), StationBarKind.Fuel),
            new($"Ore {state.Ore}/{profile.OreCap}", state.Ore / (float)Math.Max(1, profile.OreCap), StationBarKind.Progress),
        };
        if (state.Lit && state.Ore > 0)
            bars.Add(new StationBar("Smelting", state.UnitProgress / BloomeryConfig.SecondsPerOre, StationBarKind.Progress));
        return bars;
    }

    /// <summary>The bars of a crucible standing on the ground: how full it is, then one bar per metal (its molten ml, and
    /// what is still unmelted).</summary>
    public static List<StationBar>? StandingCrucible(ItemStack? vessel)
    {
        if (vessel == null) return null;
        var capacity = Math.Max(1, vessel.Item.CrucibleCapacityMl);
        var contents = vessel.Get<CrucibleComponent>();
        var bars = new List<StationBar>
        {
            new(contents.IsEmpty ? "Empty" : $"Holds {contents.TotalMl}/{capacity} ml", contents.TotalMl / (float)capacity, StationBarKind.Done),
        };
        foreach (var metal in contents.Metals)
        {
            var name = MetalName(metal.MaterialId);
            bars.Add(new StationBar(metal.PendingMl > 0 ? $"{name} {metal.MoltenMl} ml + {metal.PendingMl} ml unmelted" : $"{name} {metal.MoltenMl} ml",
                metal.TotalMl / (float)capacity, StationBarKind.Fuel));
        }
        return bars;
    }

    /// <summary>The bar of an anvil with a piece on it: how far it has been hammered.</summary>
    public static List<StationBar>? Anvil(AnvilManager.State? state) =>
        state is { HasPiece: true }
            ? [new StationBar($"Hammered {state.Hits}/{state.Profile.Hits}", state.Hits / (float)Math.Max(1, state.Profile.Hits), StationBarKind.Progress)]
            : null;

    /// <summary>The bar of a quern whose plate is turning: how far through its rotation it is. Null at rest.</summary>
    public static List<StationBar>? Quern(QuernManager.QuernState? state) =>
        state is { Spinning: true }
            ? [new StationBar("Grinding", state.Angle / MathF.Tau, StationBarKind.Progress)]
            : null;

    /// <summary>The bars of a cast mold: its pour, its cooling or its finished cast, each labeled with the metal and the
    /// milliliters it holds of the milliliters it needs. An empty mold shows every metal it casts at zero, one bar per
    /// recipe. Null for anything that is not a mold.</summary>
    public static List<StationBar>? Mold(ushort moldId, CastingManager.CastState? cast)
    {
        if (cast != null) return [MoldBar(cast)];
        if (!ContentRegistry.IsCastMold(moldId)) return null;
        var bars = new List<StationBar>();
        foreach (var recipe in ContentRegistry.CastRecipesList)
            if (recipe.MoldId == moldId)
                bars.Add(new StationBar($"{MetalName(recipe.MaterialId)} 0/{recipe.AmountMl}ml", 0f, StationBarKind.Progress));
        return bars;
    }

    // One cast cell's bar: filling, then cooling, then done
    private static StationBar MoldBar(CastingManager.CastState cast)
    {
        var label = $"{MetalName(cast.Recipe.MaterialId)} {cast.FilledMl}/{cast.Recipe.AmountMl}ml";
        if (cast.Done) return new StationBar($"{label}, ready", 1f, StationBarKind.Done);
        if (cast.Cooling) return new StationBar($"{label}, cooling", cast.Cool01, StationBarKind.Progress);
        return new StationBar(label, cast.Fill01, StationBarKind.Progress);
    }

    // A molten material's display name, for a bar's caption
    private static string MetalName(byte materialId) =>
        ContentRegistry.MoltenMaterialById(materialId)?.DisplayName ?? $"material {materialId}";

    // Seconds as m:ss
    private static string Clock(float seconds)
    {
        var whole = Math.Max(0, (int)MathF.Ceiling(seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }
}
