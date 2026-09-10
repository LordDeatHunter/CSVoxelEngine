using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>Durability component: uses left out of the item's maximum.</summary>
public record struct DurabilityComponent(int UsesLeft, int MaxUses)
{
    public float Fraction => MaxUses > 0 ? Math.Clamp(UsesLeft / (float)MaxUses, 0f, 1f) : 1f;
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
/// One item instance (ECS-spike entity): the definition id plus per-instance components.
/// Stackable pool items never get here; every <see cref="InventoryCategory.Tools"/> pickup
/// creates one of these, so durability, cooldowns and (later) enchantments/renames stay
/// per item instead of per id. Components live in a small map so new ones can be added
/// without touching the entity shape.
/// </summary>
public sealed class ItemStack
{
    public int InstanceId { get; }
    public byte DefinitionId { get; set; }

    private readonly Dictionary<Type, object> _components = new();

    public ItemStack(int instanceId, byte definitionId)
    {
        InstanceId = instanceId;
        DefinitionId = definitionId;
        if (ItemType.Types.TryGetValue(definitionId, out var type) && type.MaxDurability > 0)
            Set(new DurabilityComponent(type.MaxDurability, type.MaxDurability));
        Set(new CooldownComponent(0));
        Set(new DisplayNameComponent(null));
    }

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
