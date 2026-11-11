namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// The battle tower's forcefield dome as pure geometry, GL-free: half a spheroid standing on the parapet's top layer
/// (<see cref="BaseY"/>, the merlons' layer, so it fills the gaps between them), as wide as the tower (the same
/// <c>r² + r</c> edge <see cref="TowerShell.RingOf"/> gives the outer wall, so its base is exactly the tower's disc) and
/// <see cref="WorldGenerationConfig.TowerDomeHeight"/> tall over the center. <see cref="Holds"/> is its shell: the cells
/// inside it with a face neighbor outside it. Every face-connected path from inside to outside crosses one, so with the
/// parapet, the roof slab and the top floor's door it closes the roof off from the outside, and a player can neither
/// climb in over the wall nor leave the roof mid-fight but down the stair. <see cref="TowerGenerator"/> writes the
/// forcefield block there wherever the walls and the roof's dressing leave air.
/// </summary>
internal static class TowerDome
{
    private const int Height = WorldGenerationConfig.TowerDomeHeight;

    /// <summary>The dome's lowest layer: the parapet's top, where the merlons stand.</summary>
    internal static int BaseY(TowerSite s) => s.RoofY + WorldGenerationConfig.TowerParapetHeight;

    /// <summary>The dome's apex layer, over the center.</summary>
    internal static int TopY(TowerSite s) => BaseY(s) + Height;

    /// <summary>Whether the cell at (<paramref name="dx"/>, <paramref name="dz"/>) from the center and world height
    /// <paramref name="y"/> is part of the dome's shell.</summary>
    internal static bool Holds(TowerSite s, int dx, int y, int dz)
    {
        var dy = y - BaseY(s);
        if (dy < 0 || !Inside(s.Radius, dx, dy, dz)) return false;
        // The dome only narrows going up, so the cell below an inside cell is inside too (or under the base, the tower)
        return !Inside(s.Radius, dx + 1, dy, dz) || !Inside(s.Radius, dx - 1, dy, dz)
               || !Inside(s.Radius, dx, dy, dz + 1) || !Inside(s.Radius, dx, dy, dz - 1)
               || !Inside(s.Radius, dx, dy + 1, dz);
    }

    // (dx² + dz²) / (r² + r) + dy² / (h² + h) <= 1, in integers: the base layer is the disc the rings call inside the
    // outer wall, and the apex is exactly h over it
    private static bool Inside(int radius, int dx, int dy, int dz)
    {
        const long heightEdge = (long)Height * Height + Height;
        var radiusEdge = (long)radius * radius + radius;
        return (dx * dx + dz * dz) * heightEdge + (long)dy * dy * radiusEdge <= radiusEdge * heightEdge;
    }
}
