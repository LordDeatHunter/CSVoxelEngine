using System;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// Where a waystone traveler lands, GL-free and pure over a cell reader (<see cref="IArrivalCells"/>), shared by
/// singleplayer (<see cref="WaystoneTravel"/>) and (W-6) the server: the nearest free two-cell standing spot around the
/// destination waystone (feet and head cells <see cref="ArrivalCell.Open"/>, the cell under the feet
/// <see cref="ArrivalCell.Solid"/>, never the waystone's own column), within <see cref="SearchRadius"/> columns and
/// <see cref="SearchHeight"/> cells up or down. Nearer is better, a level step better than a climb or a drop, so a cell
/// beside the waystone on its own floor wins; ties go to the first in a fixed scan order, so the answer never depends on
/// anything but the cells. The traveler faces away from the waystone. With every cell loaded and none free the traveler
/// stands on top of the waystone; while any cell the search reads is unloaded it answers
/// <see cref="WaystoneArrivalStatus.Unloaded"/> and the caller holds the traveler.
/// </summary>
public static class WaystoneArrival
{
    /// <summary>How many columns out from the waystone the search looks (Chebyshev distance).</summary>
    public const int SearchRadius = 3;

    /// <summary>How many cells above or below the waystone's own cell a spot's feet may be.</summary>
    public const int SearchHeight = 3;

    /// <summary>How far over its floor a landed traveler's feet are put, as the spawn settle does, so the first frame
    /// never starts inside it.</summary>
    public const float LandingLift = 0.02f;

    // A step up or down costs more than the same distance across: a spot on the waystone's own floor wins
    private const float VerticalWeight = 1.5f;

    /// <summary>How many cells a waystone stands: its lower half, the cell the registry keys it by, and the upper half
    /// over it (the waystone blocks' <c>upperHalf</c>).</summary>
    public const int StoneHeight = 2;

    /// <summary>The feet of a traveler standing on top of the waystone at <paramref name="waystone"/>, over its upper
    /// half (the fallback spot, and where a traveler is held while the destination loads).</summary>
    public static Vector3 OnTopFeet((int X, int Y, int Z) waystone) =>
        new(waystone.X + 0.5f, waystone.Y + StoneHeight, waystone.Z + 0.5f);

    /// <summary>The eye of a traveler whose feet are at <paramref name="feet"/>, lifted by <see cref="LandingLift"/>.</summary>
    public static Vector3 EyeOver(Vector3 feet) => feet + new Vector3(0f, Camera.EyeHeight + LandingLift, 0f);

    /// <summary>The landing spot around the waystone at <paramref name="waystone"/> (see the class summary).</summary>
    public static WaystoneArrivalSpot Find(IArrivalCells cells, (int X, int Y, int Z) waystone)
    {
        var (wx, wy, wz) = waystone;
        for (var y = wy - SearchHeight - 1; y <= wy + SearchHeight + 1; y++)
        for (var x = wx - SearchRadius; x <= wx + SearchRadius; x++)
        for (var z = wz - SearchRadius; z <= wz + SearchRadius; z++)
        {
            if (cells.At(x, y, z) == ArrivalCell.Unloaded)
                return new WaystoneArrivalSpot(WaystoneArrivalStatus.Unloaded, default, null);
        }

        var bestCost = float.MaxValue;
        (int X, int Y, int Z) best = default;
        for (var dy = -SearchHeight; dy <= SearchHeight; dy++)
        for (var dx = -SearchRadius; dx <= SearchRadius; dx++)
        for (var dz = -SearchRadius; dz <= SearchRadius; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            var cost = dx * dx + dz * dz + VerticalWeight * dy * dy;
            if (cost >= bestCost) continue;
            var (x, y, z) = (wx + dx, wy + dy, wz + dz);
            if (cells.At(x, y, z) != ArrivalCell.Open || cells.At(x, y + 1, z) != ArrivalCell.Open
                || cells.At(x, y - 1, z) != ArrivalCell.Solid)
                continue;
            bestCost = cost;
            best = (x, y, z);
        }

        if (bestCost == float.MaxValue)
            return new WaystoneArrivalSpot(WaystoneArrivalStatus.OnTop, OnTopFeet(waystone), null);

        var away = MathF.Atan2(best.Z - wz, best.X - wx) * (180f / MathF.PI);
        return new WaystoneArrivalSpot(WaystoneArrivalStatus.Found, new Vector3(best.X + 0.5f, best.Y, best.Z + 0.5f), away);
    }
}
