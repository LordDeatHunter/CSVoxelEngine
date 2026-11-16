using System;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// Scope that points an <see cref="Inventory"/>'s <c>Selected*</c> readers at one hand: the offhand while it
/// runs a use, then back to whatever was active. Never persisted; use it in a <c>using</c>.
/// </summary>
public readonly struct HandScope : IDisposable
{
    private readonly Inventory _inventory;
    private readonly bool _previous;

    internal HandScope(Inventory inventory, bool offhand)
    {
        _inventory = inventory;
        _previous = inventory.SetOffhandActive(offhand);
    }

    public void Dispose() => _inventory.SetOffhandActive(_previous);
}
