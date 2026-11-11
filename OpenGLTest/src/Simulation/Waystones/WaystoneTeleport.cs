using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Chests;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// The waystone teleport rule, GL-free and shared by singleplayer and (W-6) the server: a player standing at a waystone
/// (<see cref="IsAtWaystone"/>) may travel to any other waystone it discovered whose block still stands, at any distance
/// and into any realm, when its mana is strictly more than half full (<see cref="PlayerMana.MoreThanHalf"/>); the trip
/// then drains the pool (<see cref="PlayerMana.Drain"/>). Creative and flying players travel free. Where the traveler
/// lands is <see cref="WaystoneArrival"/>.
/// </summary>
public static class WaystoneTeleport
{
    /// <summary>Whether a player travels free: creative or flying.</summary>
    public static bool IsFree(bool creative, bool flying) => creative || flying;

    /// <summary>Whether a player with its eye at <paramref name="eye"/> stands at the loaded waystone at the cell: it
    /// is a waystone block and within <paramref name="reach"/> of the eye (the chest's reach rule).</summary>
    public static bool IsAtWaystone(ChunkManager chunks, Vector3 eye, (int X, int Y, int Z) cell, float reach) =>
        ChestAccess.InReach(eye, cell.X, cell.Y, cell.Z, reach) && WaystoneManager.IsWaystoneAt(chunks, cell.X, cell.Y, cell.Z);

    /// <summary>
    /// Judges a trip from the waystone at <paramref name="from"/> (where the player stands; the caller checks that with
    /// <see cref="IsAtWaystone"/>) to <paramref name="to"/>: not the same waystone, discovered by this player, holding a
    /// registry row (<paramref name="destination"/>) that is not lost, and paid for unless <paramref name="free"/>.
    /// Changes nothing.
    /// </summary>
    public static WaystoneTeleportVerdict Judge(DiscoveredWaystones discovered, WaystoneRecord? destination,
        (int X, int Y, int Z) from, (int X, int Y, int Z) to, bool free, PlayerMana mana)
    {
        if (from == to) return WaystoneTeleportVerdict.Current;
        if (!discovered.Contains(to.X, to.Y, to.Z)) return WaystoneTeleportVerdict.NotDiscovered;
        if (destination == null) return WaystoneTeleportVerdict.Unknown;
        if (destination.Lost) return WaystoneTeleportVerdict.Lost;
        if (!free && !mana.MoreThanHalf) return WaystoneTeleportVerdict.NeedsMana;
        return WaystoneTeleportVerdict.Teleport;
    }

    /// <summary><see cref="Judge"/>, and on <see cref="WaystoneTeleportVerdict.Teleport"/> the trip's price: the pool is
    /// drained unless <paramref name="free"/>. A refusal changes nothing.</summary>
    public static WaystoneTeleportVerdict TryPay(DiscoveredWaystones discovered, WaystoneRecord? destination,
        (int X, int Y, int Z) from, (int X, int Y, int Z) to, bool free, PlayerMana mana)
    {
        var verdict = Judge(discovered, destination, from, to, free, mana);
        if (verdict == WaystoneTeleportVerdict.Teleport && !free)
            mana.Drain();
        return verdict;
    }
}
