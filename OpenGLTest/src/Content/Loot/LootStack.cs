using VoxelEngine.Items;

namespace VoxelEngine.Content.Loot;

/// <summary>One rolled loot result: an item id and how many, and for a piece of gear the rarity it rolled
/// (null for everything that has none).</summary>
public readonly record struct LootStack(byte Item, int Count, Rarity? Rarity);
