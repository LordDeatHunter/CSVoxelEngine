using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The live entities <see cref="RenderManager.RenderReflection"/> may draw into the water mirror,
/// handed over in one bundle so the mirror pass can filter them (near the eye, above the plane)
/// and feed their positions to the reuse cache's motion signal
/// (<see cref="ReflectionReuse.EntitiesMoved"/>). The lists are the world's own, read only.
/// </summary>
public readonly record struct ReflectionEntities(
    IReadOnlyList<ItemDrop> Drops,
    IReadOnlyList<ThrownProjectile> Thrown,
    IReadOnlyList<PrimedTnt> Primed,
    IReadOnlyList<FallingBlock> Falling,
    IReadOnlyList<Slime> Slimes,
    IReadOnlyList<TopplingTree> Toppling,
    PlayerModel? Player);
