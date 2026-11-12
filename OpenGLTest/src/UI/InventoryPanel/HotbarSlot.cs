namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// One hotbar slot: either a reference to a stackable id (blocks/misc, whose count is the
/// pool total, so several slots can show the same id and stay in sync) or to a single tool
/// instance (tools are never stacked, so slots sharing an instance morph together while
/// slots holding different instances stay independent). The default value is empty.
/// </summary>
public readonly record struct HotbarSlot(bool IsTool, ushort Id, int ToolInstance)
{
    public static HotbarSlot Empty => default;
    public bool IsEmpty => !IsTool && Id == 0;
}
