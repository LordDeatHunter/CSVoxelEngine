using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Structures;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// One accepted battle tower: where it stands, how big it rolled, what it is built of and which dressing templates its floors
/// and roof wear. Immutable, a pure function of its cell and the seed. Its members are the hooks the tower gameplay reads: the
/// floor levels and tiers (BT-4 floor locks), the stairwell door cells each floor's barrier door fills (BT-4), the altar cell
/// (BT-6) and the footprint's block and chunk range (BT-8 collapse). A main-thread or server consumer reads a cached site
/// (<c>TowerPlacement</c>), never per frame and never on a client, which streams its terrain and generates nothing. The roof
/// is open to the sky, so BT-6 must refuse the altar until every floor of the tower is cleared.
/// </summary>
public sealed class TowerSite
{
    internal TowerSite(int cellX, int cellZ, int centerX, int centerZ, int groundY, int radius, int floors, int turns,
        TowerPalette palette, StructureTemplate altar, int altarTurns, StructureTemplate[] floorTemplates, int[] floorTurns)
    {
        if (floorTemplates.Length != floors || floorTurns.Length != floors)
            throw new ArgumentException("A tower needs one floor template and one turn per floor.");
        CellX = cellX;
        CellZ = cellZ;
        CenterX = centerX;
        CenterZ = centerZ;
        GroundY = groundY;
        Radius = radius;
        Floors = floors;
        Turns = turns & 3;
        Palette = palette;
        Altar = altar;
        AltarTurns = altarTurns & 3;
        FloorTemplates = floorTemplates;
        FloorTurns = floorTurns;
    }

    /// <summary>The tower cell (in <see cref="WorldGenerationConfig.TowerCellBlocks"/> blocks) on the X axis.</summary>
    public int CellX { get; }

    /// <summary>The tower cell on the Z axis.</summary>
    public int CellZ { get; }

    /// <summary>The center column's world X.</summary>
    public int CenterX { get; }

    /// <summary>The center column's world Z.</summary>
    public int CenterZ { get; }

    /// <summary>The ground slab's world y: the ground floor stands on it.</summary>
    public int GroundY { get; }

    /// <summary>The outer wall's radius in blocks.</summary>
    public int Radius { get; }

    /// <summary>How many floors the tower has; the roof stands on top of the last one.</summary>
    public int Floors { get; }

    /// <summary>Quarter turns of the stair (and so the entrance) about the center.</summary>
    public int Turns { get; }

    /// <summary>The blocks it is built of.</summary>
    public TowerPalette Palette { get; }

    /// <summary>The dressing template of the roof around the altar.</summary>
    internal StructureTemplate Altar { get; }

    /// <summary>The altar template's quarter turns.</summary>
    internal int AltarTurns { get; }

    /// <summary>The dressing template of each floor, ground floor first.</summary>
    internal StructureTemplate[] FloorTemplates { get; }

    /// <summary>Each floor template's quarter turns.</summary>
    internal int[] FloorTurns { get; }

    /// <summary>The slab floor <paramref name="k"/> stands on (0 = the ground slab, <see cref="Floors"/> = the roof).</summary>
    public int FloorY(int k) => GroundY + k * WorldGenerationConfig.TowerFloorHeight;

    /// <summary>The roof slab's world y.</summary>
    public int RoofY => FloorY(Floors);

    /// <summary>The highest world y the tower writes: the parapet or the altar dressing, whichever is taller.</summary>
    public int TopY => RoofY + Math.Max(WorldGenerationConfig.TowerParapetHeight, Altar.Height);

    /// <summary>The lowest world y the tower writes: the foundation's bottom.</summary>
    public int BottomY => GroundY - WorldGenerationConfig.TowerFoundationDepth;

    /// <summary>The farthest a written column lies from the center: the wall plus the entrance apron.</summary>
    public int OuterRadius => Radius + WorldGenerationConfig.TowerApronDepth;

    /// <summary>The footprint box's west edge.</summary>
    public int MinX => CenterX - OuterRadius;

    /// <summary>The footprint box's east edge.</summary>
    public int MaxX => CenterX + OuterRadius;

    /// <summary>The footprint box's north edge.</summary>
    public int MinZ => CenterZ - OuterRadius;

    /// <summary>The footprint box's south edge.</summary>
    public int MaxZ => CenterZ + OuterRadius;

    /// <summary>The westmost chunk the footprint reaches.</summary>
    public int MinChunkX => VoxelCoords.WorldToChunk(MinX);

    /// <summary>The eastmost chunk the footprint reaches.</summary>
    public int MaxChunkX => VoxelCoords.WorldToChunk(MaxX);

    /// <summary>The northmost chunk the footprint reaches.</summary>
    public int MinChunkZ => VoxelCoords.WorldToChunk(MinZ);

    /// <summary>The southmost chunk the footprint reaches.</summary>
    public int MaxChunkZ => VoxelCoords.WorldToChunk(MaxZ);

    /// <summary>The lowest chunk the tower writes into.</summary>
    public int MinChunkY => VoxelCoords.WorldToChunk(BottomY);

    /// <summary>The highest chunk the tower writes into.</summary>
    public int MaxChunkY => VoxelCoords.WorldToChunk(TopY);

    /// <summary>The outer edge of the stair corridor ring (inside the outer wall).</summary>
    internal int CorridorRadius => Radius - WorldGenerationConfig.TowerWallThickness;

    /// <summary>The outer edge of the inner wall ring (inside the corridor).</summary>
    internal int InnerWallRadius => CorridorRadius - WorldGenerationConfig.TowerStairWidth;

    /// <summary>The outer edge of the rooms (inside the inner wall).</summary>
    internal int RoomRadius => InnerWallRadius - WorldGenerationConfig.TowerInnerWallThickness;

    /// <summary>How many half-steps the stair climbs from the ground slab to the roof.</summary>
    public int StairSteps => 2 * Floors * WorldGenerationConfig.TowerFloorHeight;

    /// <summary>The stair half-step level with floor <paramref name="k"/>'s slab, where its doorway opens (1..<see cref="Floors"/>;
    /// <see cref="Floors"/> is the arrival on the roof).</summary>
    public int LandingStep(int k) => 2 * k * WorldGenerationConfig.TowerFloorHeight - 1;

    /// <summary>The first of the two stair half-steps whose tube floor <paramref name="k"/>'s barrier door fills
    /// (0..<see cref="Floors"/> - 1): always odd, a full brick step, well past the floor's doorway.</summary>
    public int DoorStep(int k) => (k == 0 ? 1 : LandingStep(k)) + WorldGenerationConfig.TowerDoorStepOffset;

    /// <summary>The cell the roof altar stands in (BT-6): free in the dressing, on the roof over the center.</summary>
    public (int X, int Y, int Z) AltarCell => (CenterX, RoofY + 1, CenterZ);

    /// <summary>Floor <paramref name="floor"/>'s difficulty tier: its 0-based index.</summary>
    public int Tier(int floor) => floor;

    /// <summary>The stair tube cells floor <paramref name="floor"/>'s barrier door fills (BT-4): air in the shell, the tube
    /// over <see cref="DoorStep"/> and the step after it. Scans the disc, so a consumer keeps the result.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> DoorCells(int floor) => TowerShell.DoorCells(this, floor);
}
