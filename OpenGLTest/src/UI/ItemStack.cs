using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>Durability component: uses left out of the item's maximum.</summary>
public record struct DurabilityComponent(int UsesLeft, int MaxUses)
{
    public float Fraction => MaxUses > 0 ? MathUtils.Clamp01(UsesLeft / (float)MaxUses) : 1f;
    public bool IsBroken => MaxUses > 0 && UsesLeft <= 0;
}

/// <summary>Cooldown component: earliest scene time the item may be used again.</summary>
public record struct CooldownComponent(double NextUseAt)
{
    public bool Ready(double now) => now >= NextUseAt;
}

/// <summary>Display component: optional player rename layered over the definition name.</summary>
public record struct DisplayNameComponent(string? CustomName)
{
    public string Resolve(string definitionName) =>
        string.IsNullOrWhiteSpace(CustomName) ? definitionName : CustomName!;
}

/// <summary>
/// One item instance: the <see cref="Item"/> singleton it is plus the per-stack metadata that
/// makes this existence distinct — count, durability, cooldown, rename and enchantments.
/// The definition never changes out from under the stack; <see cref="SetItem"/> (morphing a
/// bucket full, pouring it back out) swaps both together. Components live in a small map so new
/// ones can be added without touching the entity shape.
/// </summary>
public sealed class ItemStack
{
    public int InstanceId { get; }

    /// <summary>The singleton this stack is an instance of; never null past construction.</summary>
    public Item Item { get; private set; }

    /// <summary>Definition id, mirroring <see cref="Item.Id"/> for byte-oriented callers.</summary>
    public byte DefinitionId => Item.Id;

    /// <summary>Units in this stack. Tool instances stay at 1; pool counts live in
    /// <see cref="Inventory"/>, which hands out one stack view per tool.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Enchantment levels by id. Data only for now: nothing reads them yet, but the map
    /// travels with the stack so a future table or anvil has somewhere to write.</summary>
    public Dictionary<string, int> Enchantments { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<Type, object> _components = new();

    public ItemStack(int instanceId, byte definitionId)
        : this(instanceId, ItemRegistry.TryGet(definitionId)
            ?? throw new InvalidOperationException(
                $"No item with id {definitionId} is registered; content must load first."))
    {
    }

    public ItemStack(int instanceId, Item item)
    {
        InstanceId = instanceId;
        Item = item;
        if (item.MaxDurability > 0)
            Set(new DurabilityComponent(item.MaxDurability, item.MaxDurability));
        Set(new CooldownComponent(0));
        Set(new DisplayNameComponent(null));
    }

    /// <summary>Morphs the stack into another item in place, topping durability up when the new
    /// form has some and this stack had none (a poured-out bucket stays endless, a fresh
    /// firestarter starts full).</summary>
    public void SetItem(Item item)
    {
        Item = item;
        if (item.MaxDurability > 0 && !UsesDurability)
            Set(new DurabilityComponent(item.MaxDurability, item.MaxDurability));
    }

    /// <summary>Layers a player rename over the definition name; null clears it.</summary>
    public void Rename(string? customName) => Set(new DisplayNameComponent(customName));

    /// <summary>Rename if set, else the definition name.</summary>
    public string ResolvedName => Get<DisplayNameComponent>().Resolve(Item.Name);

    /// <summary>Enchantment level for this id, or 0 when absent.</summary>
    public int EnchantmentLevel(string enchantment) =>
        Enchantments.GetValueOrDefault(enchantment);

    public void Set<T>(T component) where T : struct => _components[typeof(T)] = component;

    public T Get<T>() where T : struct =>
        _components.TryGetValue(typeof(T), out var value) ? (T)value : default;

    public bool UsesDurability => Get<DurabilityComponent>().MaxUses > 0;

    public float DurabilityFraction => Get<DurabilityComponent>().Fraction;

    public bool CanUseAt(double now)
    {
        var cooldown = Get<CooldownComponent>();
        if (!cooldown.Ready(now)) return false;
        return !Get<DurabilityComponent>().IsBroken;
    }

    public void MarkUsed(double now, float delay) => Set(new CooldownComponent(now + delay));

    /// <summary>Spends durability uses; returns true when the item broke.</summary>
    public bool Damage(int amount = 1)
    {
        var durability = Get<DurabilityComponent>();
        if (durability.MaxUses <= 0) return false;
        durability.UsesLeft = Math.Max(0, durability.UsesLeft - amount);
        Set(durability);
        return durability.IsBroken;
    }
}
