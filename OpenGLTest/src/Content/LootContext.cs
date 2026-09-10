namespace silkgltriangle;

/// <summary>
/// Where a loot roll happens, in world block coordinates. Count providers that depend on the
/// world (pebbles recompute their flint yield from the position hash) read it; plain tables
/// ignore it. <paramref name="Count"/> carries an explicit pile size when the destroyed block
/// was a player-built stack (state byte 1..max); 0 means "derive it", which is what untouched
/// worldgen reads as.
/// </summary>
public readonly record struct LootContext(int X, int Y, int Z, int Count = 0);
