using System.Numerics;

namespace VoxelEngine.Player;

/// <summary>
/// Solids that are not blocks which a player body collides with (the iceberg boxes): the client's
/// own physics (<see cref="BodyCollision"/>, through <see cref="Camera.BodySolids"/>) and the
/// server's grounded probe (<see cref="PlayerBodyProbe.Grounded"/>) both read it, so a body
/// standing on one is grounded on both sides. Main thread only.
/// </summary>
public interface IBodySolids
{
    /// <summary>Whether the box from <paramref name="min"/> to <paramref name="max"/> overlaps any of these solids.</summary>
    bool Overlaps(Vector3 min, Vector3 max);
}
