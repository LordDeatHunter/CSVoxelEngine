namespace silkgltriangle;

/// <summary>
/// Where a loot roll happens, in world block coordinates. Count providers that depend on the
/// world (pebbles recompute their yield from the position hash) read it; plain tables
/// ignore it. <paramref name="Count"/> carries an explicit pile size when the destroyed block
/// was a player-built stack (state byte 1..max); 0 means "derive it", which is what untouched
/// worldgen reads as. <paramref name="Secondary"/> carries the obsidian share of a player-built
/// pebble pile; 0 means none, or derive it for worldgen. <paramref name="State"/> carries the
/// destroyed cell's state byte, which tree wood reads for its thickness loot band (see
/// <see cref="WoodLootBand"/>); 0 for everything else.
/// </summary>
public readonly record struct LootContext(int X, int Y, int Z, int Count = 0, int Secondary = 0, byte State = 0);
