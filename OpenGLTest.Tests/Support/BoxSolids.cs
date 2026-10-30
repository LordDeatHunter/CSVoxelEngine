using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Player;

namespace VoxelEngine.Tests.Support;

/// <summary>A test <see cref="IBodySolids"/>: a fixed list of world-space boxes.</summary>
internal sealed class BoxSolids : IBodySolids
{
    public List<(Vector3 Min, Vector3 Max)> Boxes { get; } = [];

    public BoxSolids Add(Vector3 min, Vector3 max)
    {
        Boxes.Add((min, max));
        return this;
    }

    public bool Overlaps(Vector3 min, Vector3 max)
    {
        foreach (var (boxMin, boxMax) in Boxes)
            if (IcebergContact.Overlaps(min, max, boxMin, boxMax))
                return true;
        return false;
    }
}
