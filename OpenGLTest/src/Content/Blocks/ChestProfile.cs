namespace VoxelEngine.Content.Blocks;

/// <summary>
/// What makes a block a chest, from its <c>chest</c> JSON object: how many slots it holds and, for a ruin chest, the
/// loot table it rolls the first time it is opened or broken (see <c>ChestManager</c>). A crafted chest names no table
/// and starts empty.
/// </summary>
/// <param name="Slots">How many stacks it holds.</param>
/// <param name="LootTable">The loot table file name a never-opened chest rolls, or null.</param>
public sealed record ChestProfile(int Slots, string? LootTable);
