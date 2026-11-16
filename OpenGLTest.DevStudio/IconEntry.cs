namespace VoxelEngine.DevStudio;

/// <summary>One entry of the QUESTS tab's icon grid: its kind, its block or item id (0 for an entity), its display name,
/// and <see cref="Key"/>, the name a quest file uses for it (a block's bare name, an item's <c>item/</c> name, an
/// entity's file name).</summary>
public readonly record struct IconEntry(IconKind Kind, ushort Id, string Name, string Key);
