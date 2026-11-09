using System.Numerics;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// A left press on a listed row of the [E] panel, which lifts into a drag once the pointer leaves the press by
/// <see cref="LiftDistance"/>: the row's item then follows the pointer and goes into the hotbar or equipment slot it is
/// released over (<see cref="RowEquip.DropOnHotbar"/>, <see cref="RowEquip.DropOnEquipment"/>). Until it lifts, the
/// release is an ordinary click. GL-free.
/// </summary>
public sealed class RowDrag(byte id, int toolInstance, Vector2 pressCanvas)
{
    /// <summary>Canvas units the pointer must travel from the press before the row lifts.</summary>
    public const float LiftDistance = 6f;

    /// <summary>The dragged row's item id.</summary>
    public byte Id { get; } = id;

    /// <summary>The tool or gear instance the row names, else 0.</summary>
    public int ToolInstance { get; } = toolInstance;

    /// <summary>Whether the pointer has left the press: the release is a drop, not a click.</summary>
    public bool Lifted { get; private set; }

    /// <summary>Follows the pointer (canvas units); once lifted, the drag stays lifted until released.</summary>
    public void Track(Vector2 pointerCanvas)
    {
        if (!Lifted && Vector2.DistanceSquared(pointerCanvas, pressCanvas) >= LiftDistance * LiftDistance)
            Lifted = true;
    }
}
