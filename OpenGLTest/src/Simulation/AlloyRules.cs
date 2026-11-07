using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Recipes;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation;

/// <summary>
/// The pure crucible rules a kiln runs, GL-free and registry-free (the alloy recipes are passed
/// in; <see cref="KilnManager"/> hands it <c>ContentRegistry.AlloyRecipes</c>):
/// <list type="bullet">
/// <item>what a vessel may take (<see cref="CanMix"/>): a plain kiln keeps it to one metal, an
/// alloy kiln also lets the parts and the result of one alloy share it, charged in any order;</item>
/// <item>which metal the next melt chunk melts (<see cref="NextToMelt"/>): in an alloy kiln a part
/// waits, unmelted, while another part of its alloy is in the vessel, so a lone metal still melts
/// as in a plain kiln;</item>
/// <item>alloying (<see cref="Resolve"/>): every full set of parts, molten or pending, becomes the
/// same ml of the result as pending charge, which then melts at the kiln's pace. Whatever does not
/// make a full set stays where it is, and the ml in equals the ml out.</item>
/// </list>
/// </summary>
public static class AlloyRules
{
    /// <summary>
    /// Whether a vessel holding <paramref name="contents"/> may take a charge of
    /// <paramref name="materialId"/>: always when empty or holding only that metal, and in an
    /// alloy kiln (<paramref name="canAlloy"/>) also when every metal it would then hold is a part
    /// or the result of one alloy. Capacity is the caller's check.
    /// </summary>
    public static bool CanMix(CrucibleComponent contents, byte materialId, bool canAlloy,
        IReadOnlyList<AlloyRecipe> alloys)
    {
        if (contents.IsEmpty) return true;
        if (contents.Metals.Count == 1 && contents.Holds(materialId)) return true;
        if (!canAlloy) return false;
        foreach (var alloy in alloys)
            if (alloy.Involves(materialId) && contents.Metals.All(m => alloy.Involves(m.MaterialId)))
                return true;
        return false;
    }

    /// <summary>
    /// The metal the next melt chunk melts, or 0 when nothing may melt: the lowest-id metal with
    /// pending charge that is not waiting for its alloy (<see cref="WaitsForAlloy"/>).
    /// </summary>
    public static byte NextToMelt(CrucibleComponent contents, bool canAlloy, IReadOnlyList<AlloyRecipe> alloys)
    {
        foreach (var metal in contents.Metals)
        {
            if (metal.PendingMl <= 0) continue;
            if (canAlloy && WaitsForAlloy(contents, metal.MaterialId, alloys)) continue;
            return metal.MaterialId;
        }
        return 0;
    }

    /// <summary>Whether <paramref name="materialId"/> is a part of an alloy another of whose parts
    /// is also in the vessel: an alloy kiln keeps such a charge solid until its set is full.</summary>
    public static bool WaitsForAlloy(CrucibleComponent contents, byte materialId, IReadOnlyList<AlloyRecipe> alloys)
    {
        foreach (var alloy in alloys)
        {
            if (!alloy.HasPart(materialId)) continue;
            foreach (var (part, _) in alloy.Parts)
                if (part != materialId && contents.Holds(part)) return true;
        }
        return false;
    }

    /// <summary>
    /// Turns every full set of each alloy's parts into the alloy: per set, each part's ml is taken
    /// from its pending charge first and then from its molten pool, and the set's ml is added to
    /// the result as pending charge. Alloys are applied in list order; what does not make a full
    /// set is left as it was. The total ml never changes.
    /// </summary>
    public static CrucibleComponent Resolve(CrucibleComponent contents, IReadOnlyList<AlloyRecipe> alloys)
    {
        foreach (var alloy in alloys)
        {
            var sets = int.MaxValue;
            foreach (var (part, ml) in alloy.Parts)
                sets = Math.Min(sets, (contents.MoltenOf(part) + contents.PendingOf(part)) / ml);
            if (sets <= 0 || sets == int.MaxValue) continue;

            foreach (var (part, ml) in alloy.Parts)
            {
                var take = sets * ml;
                var fromPending = Math.Min(take, contents.PendingOf(part));
                contents = contents.With(part, -(take - fromPending), -fromPending);
            }
            contents = contents.With(alloy.ResultId, 0, sets * alloy.SetMl);
        }
        return contents;
    }
}
