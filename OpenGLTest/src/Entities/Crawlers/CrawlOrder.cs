using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// What a crawling mob wants of its <see cref="CrawlDrive"/> for one frame.
/// </summary>
/// <param name="Move">The brain's move: zero holds still, anything else crawls (a stroll hands in a fraction of a
/// unit move, which scales the speed).</param>
/// <param name="Target">The chased target, or null while it wanders.</param>
/// <param name="Wander">The point a stroll heads for.</param>
/// <param name="Dying">A dying body lets go of a wall or a ceiling and takes hold of nothing.</param>
/// <param name="Speed">The crawl speed along a surface.</param>
/// <param name="MeleeReach">How far from the target's chest the kind's melee lands, where a planned path may end.</param>
/// <param name="Facing">The heading's angle the body takes on the first surface it finds (a spawn, a restore).</param>
public readonly record struct CrawlOrder(Vector3 Move, IMobTarget? Target, Vector3? Wander, bool Dying, float Speed,
    float MeleeReach, float Facing);
