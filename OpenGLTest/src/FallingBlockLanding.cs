using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A falling cube that just settled back into the world as a block (<see cref="FallingBlockManager.Landings"/>):
/// the cell it landed in, the block, how far it fell (<see cref="FallingBlock.FallBlocks"/>) and
/// whether it settled under water, where no dust rises.
/// </summary>
public readonly record struct FallingBlockLanding(Vector3 Cell, byte BlockId, float FallBlocks, bool Submerged);
