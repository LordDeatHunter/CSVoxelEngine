using System;
using System.Collections.Generic;
using VoxelEngine.Content.Spells;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// A spell staff. Each of its <see cref="Slots"/> holds one bound spell recipe, carried as the
/// <see cref="StaffComponent"/> on the instance's <see cref="ItemStack"/>, so two staffs are never
/// interchangeable and a staff stores as a tool instance like a crucible. Which spell parts it
/// accepts is decided by its <see cref="AllowedTags"/> (<see cref="SpellPart.IsAllowedBy"/>); the
/// binding rules live in <see cref="StaffBinding"/>.
/// </summary>
public sealed class StaffItem : Item
{
    internal StaffItem(ItemFactory.ItemSpec spec) : base(spec)
    {
        Slots = spec.StaffSlots;
        Tier = spec.StaffTier;
        AllowedTags = spec.StaffTags ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>How many spell recipes the staff holds, one per slot.</summary>
    public int Slots { get; }

    /// <summary>The staff's tier, 1 or more: what the tooltip shows; the tags do the gating.</summary>
    public int Tier { get; }

    /// <summary>The spell part tags the staff allows; a part passes when any of its tags is here.</summary>
    public IReadOnlySet<string> AllowedTags { get; }

    /// <summary>One inventory entry per staff, so each one's bound spells stay its own.</summary>
    public override InventoryCategory StorageCategory => InventoryCategory.Tools;
}
