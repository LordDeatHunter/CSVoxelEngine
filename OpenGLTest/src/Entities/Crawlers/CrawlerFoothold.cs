using System.Numerics;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// One step of a crawler's path (<see cref="CrawlerPathfinder"/>): the air cell its body fills and the face it clings
/// to there, as the <see cref="CrawlFrame"/> wire index of that face's outward normal (the solid cell is the one
/// behind it). <paramref name="Drop"/> marks a foothold reached by letting go of the one before and falling straight
/// down onto it.
/// </summary>
public readonly record struct CrawlerFoothold(int X, int Y, int Z, int Face, bool Drop = false)
{
    /// <summary>The outward normal of the face held here.</summary>
    public Vector3 Normal => CrawlFrame.NormalOf(Face);

    /// <summary>Where the center of a body of <paramref name="halfExtent"/> clinging here sits: against the face.</summary>
    public Vector3 Center(float halfExtent) =>
        new Vector3(X + 0.5f, Y + 0.5f, Z + 0.5f) - Normal * (0.5f - halfExtent);

    /// <summary>Whether this is the face <paramref name="face"/> of cell (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>).</summary>
    public bool Is(int x, int y, int z, int face) => X == x && Y == y && Z == z && Face == face;

    /// <summary>Whether this foothold is in the same cell as <paramref name="other"/> (a turn onto another face).</summary>
    public bool SameCell(CrawlerFoothold other) => X == other.X && Y == other.Y && Z == other.Z;
}
