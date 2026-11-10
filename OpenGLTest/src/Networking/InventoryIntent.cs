using VoxelEngine.Networking.ServerSide;

namespace VoxelEngine.Networking;

/// <summary>
/// One inventory intent a client sends (<see cref="NetMessage.InventoryRequest"/>): what the player
/// did, never its result. The server replays it against that player's own inventory through
/// <see cref="InventoryIntentRules"/> and answers with a full snapshot either way, so a refused
/// intent rubberbands the client's optimistic copy. Which fields an action reads:
/// <list type="bullet">
/// <item><see cref="NetInventoryAction.SelectSlot"/>: <see cref="Slot"/>.</item>
/// <item><see cref="NetInventoryAction.DropSelected"/> / <see cref="NetInventoryAction.DropAllSelected"/>: none.</item>
/// <item><see cref="NetInventoryAction.Equip"/>: <see cref="Slot"/>, <see cref="IsTool"/> and <see cref="Id"/>
/// (a stackable's item id, or a tool's instance id).</item>
/// <item><see cref="NetInventoryAction.Craft"/>: <see cref="Recipe"/> (the recipe's file name),
/// <see cref="Count"/> (the batch) and <see cref="Members"/> (the member id fed into each ingredient,
/// in ingredient order).</item>
/// <item><see cref="NetInventoryAction.CreativeGrant"/>: <see cref="Id"/> and <see cref="Count"/>.</item>
/// <item><see cref="NetInventoryAction.CreativeClear"/> / <see cref="NetInventoryAction.PickBlock"/>: <see cref="Id"/>.</item>
/// <item><see cref="NetInventoryAction.SelectStaffSlot"/>: <see cref="Slot"/>, the held staff's spell
/// slot to select.</item>
/// <item><see cref="NetInventoryAction.BindStaffSlot"/>: <see cref="Slot"/> and <see cref="Parts"/>, the
/// spell part keys (<c>&lt;kind&gt;/&lt;name&gt;</c>) to bind there.</item>
/// <item><see cref="NetInventoryAction.ClearStaffSlot"/>: <see cref="Slot"/>, the staff slot to unbind.</item>
/// <item><see cref="NetInventoryAction.EquipGear"/>: <see cref="Slot"/> (an equipment slot) and <see cref="Id"/>
/// (a gear instance id); <see cref="NetInventoryAction.UnequipGear"/>: <see cref="Slot"/>.</item>
/// </list>
/// Record equality compares <see cref="Members"/> and <see cref="Parts"/> by reference, so compare
/// those fields' contents instead.
/// </summary>
public readonly record struct InventoryIntent(
    NetInventoryAction Action,
    int Slot = 0,
    bool IsTool = false,
    int Id = 0,
    int Count = 0,
    string? Recipe = null,
    ushort[]? Members = null,
    string[]? Parts = null)
{
    public static InventoryIntent Select(int slot) => new(NetInventoryAction.SelectSlot, Slot: slot);

    public static InventoryIntent Drop(bool wholeStack) =>
        new(wholeStack ? NetInventoryAction.DropAllSelected : NetInventoryAction.DropSelected);

    public static InventoryIntent EquipStackable(int slot, ushort id) =>
        new(NetInventoryAction.Equip, Slot: slot, IsTool: false, Id: id);

    public static InventoryIntent EquipTool(int slot, int instanceId) =>
        new(NetInventoryAction.Equip, Slot: slot, IsTool: true, Id: instanceId);

    public static InventoryIntent Craft(string recipe, int batch, ushort[] members) =>
        new(NetInventoryAction.Craft, Count: batch, Recipe: recipe, Members: members);

    public static InventoryIntent CreativeGrant(ushort id, int count) =>
        new(NetInventoryAction.CreativeGrant, Id: id, Count: count);

    public static InventoryIntent CreativeClear(ushort id) => new(NetInventoryAction.CreativeClear, Id: id);

    public static InventoryIntent PickBlock(ushort id) => new(NetInventoryAction.PickBlock, Id: id);

    public static InventoryIntent SelectStaffSlot(int slot) => new(NetInventoryAction.SelectStaffSlot, Slot: slot);

    public static InventoryIntent BindStaffSlot(int slot, string[] parts) =>
        new(NetInventoryAction.BindStaffSlot, Slot: slot, Parts: parts);

    public static InventoryIntent ClearStaffSlot(int slot) => new(NetInventoryAction.ClearStaffSlot, Slot: slot);

    public static InventoryIntent EquipGear(int equipmentSlot, int instanceId) =>
        new(NetInventoryAction.EquipGear, Slot: equipmentSlot, Id: instanceId);

    public static InventoryIntent UnequipGear(int equipmentSlot) =>
        new(NetInventoryAction.UnequipGear, Slot: equipmentSlot);
}
