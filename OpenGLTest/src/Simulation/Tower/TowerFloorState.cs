using System.Collections.Generic;
using VoxelEngine.Entities.TowerMobs;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The lock state of one tower floor: its band, its skeleton piles, the door cells that seal it from the floor above,
/// how many of its skeletons live or are owed back, and whether its door is open. On the authoritative side the piles,
/// door cells and counts are live; a client's mirror keeps only the band, the counts and the door bit read from the
/// wire, with no piles and no door cells. Main thread only.
/// </summary>
public sealed class TowerFloorState
{
    internal TowerFloorState(int floor, int bottomY, int topY, TowerFloorRef reference, PileState[] piles,
        IReadOnlyList<(int X, int Y, int Z)> doorCells)
    {
        Floor = floor;
        BottomY = bottomY;
        TopY = topY;
        Ref = reference;
        Piles = piles;
        DoorCells = doorCells;
        PilesLeft = CountPilesLeft();
    }

    /// <summary>The floor's index (0 = the ground floor).</summary>
    public int Floor { get; }

    /// <summary>The slab the floor stands on: the band is above it.</summary>
    public int BottomY { get; }

    /// <summary>The top of the floor's band (the next floor's slab).</summary>
    public int TopY { get; }

    /// <summary>The tower and floor its skeletons are bound to.</summary>
    public TowerFloorRef Ref { get; }

    /// <summary>The floor's skeleton piles, in the site's pile order; empty on a mirror.</summary>
    public PileState[] Piles { get; }

    /// <summary>Every cell of the door that seals this floor from the one above; empty on a mirror.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> DoorCells { get; }

    /// <summary>The door cells still to clear: empty while the door is closed, filled from <see cref="DoorCells"/> when
    /// it opens and emptied by the capped sweep.</summary>
    public List<(int X, int Y, int Z)> DoorRemaining { get; } = [];

    /// <summary>The living skeletons bound to this floor, recounted each frame.</summary>
    public int Living { get; set; }

    /// <summary>Bound skeletons that despawned or strayed and must be raised again (saved).</summary>
    public int Owed { get; set; }

    /// <summary>The piles that still have a wave; computed on the authoritative side, read from the wire on a mirror.</summary>
    public int PilesLeft { get; set; }

    /// <summary>Skeletons left to beat (living, owed and pending a restore); computed on the authoritative side, read
    /// from the wire on a mirror.</summary>
    public int Alive { get; set; }

    /// <summary>Whether the door sealing this floor from the next is open (saved; it stays open).</summary>
    public bool DoorOpen { get; set; }

    /// <summary>The piles in <see cref="Piles"/> that still have a wave.</summary>
    internal int CountPilesLeft()
    {
        var left = 0;
        for (var i = 0; i < Piles.Length; i++)
            if (!Piles[i].Depleted) left++;
        return left;
    }
}
