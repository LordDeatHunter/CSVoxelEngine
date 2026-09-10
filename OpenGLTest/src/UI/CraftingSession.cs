using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The crafting tab's working state: which recipe is open, how many copies are wanted, and
/// what the player has "fed" into each ingredient slot. Fed items are only a reservation —
/// nothing leaves the pools until <see cref="Craft"/> runs. Switching recipes clears the
/// reservation; closing the panel does too.
/// </summary>
public sealed class CraftingSession
{
    public const int MaxBatch = 99;

    public CraftingRecipe? Recipe { get; private set; }

    public int Batch { get; private set; } = 1;

    private readonly Dictionary<int, FedEntry> _fed = new();

    /// <summary>What an ingredient slot holds: the picked member id plus reserved units.</summary>
    public readonly record struct FedEntry(byte MemberId, int Count);

    public FedEntry FedAt(int index) => _fed.GetValueOrDefault(index);

    /// <summary>Units of one craft this slot needs at the current batch.</summary>
    public int NeedAt(int index) =>
        Recipe == null ? 0 : Recipe.Ingredients[index].Count * Batch;

    /// <summary>
    /// Opens a recipe, clearing any previous reservation. Opening the already-open recipe
    /// keeps its fed state; opening nothing clears everything.
    /// </summary>
    public void Select(CraftingRecipe? recipe)
    {
        if (ReferenceEquals(Recipe, recipe)) return;
        Recipe = recipe;
        Batch = 1;
        _fed.Clear();
    }

    public void Clear()
    {
        Recipe = null;
        Batch = 1;
        _fed.Clear();
    }

    /// <summary>Desired copies, clamped to 1..99. Affordability is what gates the button, not this.</summary>
    public void SetBatch(int batch) => Batch = Math.Clamp(batch, 1, MaxBatch);

    /// <summary>
    /// Picks which member a slot takes. Keeps the fed count, clamped to what is on hand.
    /// Ignored for unknown slots or ids the ingredient does not accept.
    /// </summary>
    public void SetMember(int index, byte memberId, Inventory inventory)
    {
        if (Recipe == null || (uint)index >= (uint)Recipe.Ingredients.Length) return;
        if (!Recipe.Ingredients[index].Accepts(memberId)) return;
        var fed = FedAt(index);
        var count = Math.Min(fed.Count, AvailableToFeed(memberId, inventory, exceptIndex: index));
        _fed[index] = new FedEntry(memberId, count);
    }

    /// <summary>Reserves one more unit into a slot, picking a stocked member when none is picked yet.</summary>
    public void FeedMore(int index, Inventory inventory)
    {
        if (Recipe == null || (uint)index >= (uint)Recipe.Ingredients.Length) return;
        var fed = FedAt(index);
        var member = fed.MemberId != 0
            ? fed.MemberId
            : FirstStockedMember(Recipe.Ingredients[index], inventory);
        if (member == 0 || fed.Count >= NeedAt(index)) return;
        if (AvailableToFeed(member, inventory, exceptIndex: index) <= fed.Count) return;
        _fed[index] = new FedEntry(member, fed.Count + 1);
    }

    /// <summary>Releases one reserved unit from a slot.</summary>
    public void FeedLess(int index)
    {
        if (!_fed.TryGetValue(index, out var fed) || fed.Count <= 0) return;
        if (fed.Count == 1) _fed.Remove(index);
        else _fed[index] = fed with { Count = fed.Count - 1 };
    }

    /// <summary>
    /// Feeds one unit of this inventory id into the first slot that still needs it: a slot
    /// accepting the id, holding none of another member, and still short of its need. Returns
    /// whether anything was fed.
    /// </summary>
    public bool FeedFromInventory(byte id, Inventory inventory)
    {
        if (Recipe == null || id == 0) return false;
        for (var i = 0; i < Recipe.Ingredients.Length; i++)
        {
            if (!Recipe.Ingredients[i].Accepts(id)) continue;
            var fed = FedAt(i);
            if (fed.MemberId != 0 && fed.MemberId != id) continue;
            if (fed.Count >= NeedAt(i)) continue;
            if (AvailableToFeed(id, inventory, exceptIndex: i) <= fed.Count) continue;
            _fed[i] = new FedEntry(id, fed.Count + 1);
            return true;
        }
        return false;
    }

    /// <summary>Copies the player could afford from raw pools, ignoring the reservation.</summary>
    public int MaxAffordable(Inventory inventory)
    {
        if (Recipe == null) return 0;
        var max = int.MaxValue;
        foreach (var ingredient in Recipe.Ingredients)
        {
            var available = 0;
            foreach (var id in ingredient.AcceptedIds)
                available += inventory.CountFor(id);
            max = Math.Min(max, available / Math.Max(1, ingredient.Count));
        }
        var category = Inventory.CategoryOf(Recipe.ResultId);
        if (category.HasValue)
        {
            var net = Recipe.ResultCount - FreedPerBatch(category.Value);
            if (net > 0 && max > 0)
                max = Math.Min(max,
                    (Inventory.CapacityOf(category.Value) - inventory.UsedOf(category.Value)) / net);
        }
        return Math.Max(0, max);
    }

    /// <summary>Whether [CRAFT] may run: every slot fed to its need, stock covering the
    /// reservation, and room for the results.</summary>
    public bool IsReady(Inventory inventory)
    {
        if (Recipe == null || Batch < 1) return false;
        for (var i = 0; i < Recipe.Ingredients.Length; i++)
        {
            if (FedAt(i).Count < NeedAt(i)) return false;
        }
        foreach (var (member, reserved) in ReservedTotals())
        {
            if (inventory.CountFor(member) < reserved) return false;
        }
        var category = Inventory.CategoryOf(Recipe.ResultId);
        if (!category.HasValue) return false;
        return inventory.UsedOf(category.Value) - FreedPerBatch(category.Value) * Batch
            + Recipe.ResultCount * Batch <= Inventory.CapacityOf(category.Value);
    }

    /// <summary>One line for under the [CRAFT] button: what is missing, or what is ready.</summary>
    public string StatusLine(Inventory inventory)
    {
        if (Recipe == null) return "Pick a recipe on the left";
        for (var i = 0; i < Recipe.Ingredients.Length; i++)
        {
            var need = NeedAt(i);
            var fed = FedAt(i);
            if (fed.Count >= need) continue;
            var name = FedName(i, inventory);
            var owned = OwnedFor(i, inventory);
            if (owned < need)
                return $"Need {need - owned} more {name} ({owned}/{need} on hand)";
            return $"Feed {need - fed.Count} more {name} ({fed.Count}/{need} fed)";
        }
        foreach (var (member, reserved) in ReservedTotals())
        {
            if (inventory.CountFor(member) < reserved)
                return $"Not enough {Inventory.DisplayName(member)} anymore";
        }
        var category = Inventory.CategoryOf(Recipe.ResultId);
        if (category.HasValue && inventory.UsedOf(category.Value)
                - FreedPerBatch(category.Value) * Batch + Recipe.ResultCount * Batch
                > Inventory.CapacityOf(category.Value))
            return "No room for the result";
        var total = Recipe.ResultCount * Batch;
        return Batch > 1 ? $"Ready — craft {Batch}x for {total} {Inventory.DisplayName(Recipe.ResultId)}"
            : $"Ready — craft {total} {Inventory.DisplayName(Recipe.ResultId)}";
    }

    /// <summary>
    /// Runs the craft through the inventory, consuming exactly the fed members. On success the
    /// reservation is reduced by what was spent (the member picks stay), so another press
    /// crafts again once re-fed. Returns false when not ready, consuming nothing.
    /// </summary>
    public bool Craft(Inventory inventory)
    {
        if (!IsReady(inventory) || Recipe == null) return false;
        var recipe = Recipe;
        var fed = new Dictionary<int, FedEntry>(_fed);
        var ok = inventory.TryCraft(recipe, Batch, (index, _) => fed[index].MemberId);
        if (!ok) return false;
        foreach (var (index, entry) in fed)
        {
            var left = entry.Count - recipe.Ingredients[index].Count * Batch;
            if (left <= 0) _fed.Remove(index);
            else _fed[index] = entry with { Count = left };
        }
        return true;
    }

    private byte FirstStockedMember(CraftingRecipe.Ingredient ingredient, Inventory inventory)
    {
        foreach (var id in ingredient.AcceptedIds)
            if (inventory.CountFor(id) > 0)
                return id;
        return ingredient.AcceptedIds.Length > 0 ? ingredient.AcceptedIds[0] : (byte)0;
    }

    private int OwnedFor(int index, Inventory inventory)
    {
        if (Recipe == null) return 0;
        var total = 0;
        foreach (var id in Recipe.Ingredients[index].AcceptedIds)
            total += inventory.CountFor(id);
        return total;
    }

    private string FedName(int index, Inventory inventory)
    {
        if (Recipe == null) return "ingredient";
        var ingredient = Recipe.Ingredients[index];
        if (!ingredient.IsTag) return Inventory.DisplayName(ingredient.Id);
        var fed = FedAt(index);
        if (fed.MemberId != 0) return Inventory.DisplayName(fed.MemberId);
        return $"any {ingredient.Tag}";
    }

    private int AvailableToFeed(byte member, Inventory inventory, int exceptIndex)
    {
        var reservedElsewhere = 0;
        foreach (var (index, fed) in _fed)
            if (index != exceptIndex && fed.MemberId == member)
                reservedElsewhere += fed.Count;
        return Math.Max(0, inventory.CountFor(member) - reservedElsewhere);
    }

    private Dictionary<byte, int> ReservedTotals()
    {
        var totals = new Dictionary<byte, int>();
        foreach (var (_, fed) in _fed)
        {
            if (fed.MemberId == 0 || fed.Count <= 0) continue;
            totals[fed.MemberId] = totals.GetValueOrDefault(fed.MemberId) + fed.Count;
        }
        return totals;
    }

    private int FreedPerBatch(InventoryCategory category)
    {
        if (Recipe == null) return 0;
        var freed = 0;
        foreach (var ingredient in Recipe.Ingredients)
        {
            if (ingredient.IsTag)
            {
                var allSame = true;
                foreach (var id in ingredient.AcceptedIds)
                    if (Inventory.CategoryOf(id) != category)
                    {
                        allSame = false;
                        break;
                    }
                if (allSame) freed += ingredient.Count;
            }
            else if (Inventory.CategoryOf(ingredient.Id) == category) freed += ingredient.Count;
        }
        return freed;
    }
}
