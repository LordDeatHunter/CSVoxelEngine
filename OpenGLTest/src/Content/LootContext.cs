namespace silkgltriangle;

/// <summary>
/// Where a loot roll happens, in world block coordinates. Count providers that depend on the
/// world (pebbles recompute their flint yield from the position hash) read it; plain tables
/// ignore it.
/// </summary>
public readonly record struct LootContext(int X, int Y, int Z);
