using System;
using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// Authoring contract of one kiln model: where its opening is and where the things it holds sit
/// inside it. Every number mirrors the anchor's model, authored with the opening on north (-Z),
/// so these are cell-space values in the volume's local frame, 0..<see cref="VolumeSize"/> on
/// every axis, and <see cref="ToWorld"/> turns one of them onto the kiln's actual facing.
///
/// <para>
/// One layout per structure size: <see cref="Kiln"/> mirrors
/// <c>assets/models/block/mud_brick_kiln.json</c> (2x2x2, 0..32 model units) and
/// <see cref="Furnace"/> mirrors <c>assets/models/block/alloy_furnace.json</c> (3x3x3, 0..48),
/// so a kiln anchor's model must follow the layout of its <c>multiblock.size</c>
/// (<see cref="ForSize"/>, read into <see cref="KilnProfile.Layout"/> at load). The crucible
/// bowl and the fuel lumps are the same items in every kiln, so their sizes stay on
/// <see cref="KilnRenderer"/>.
/// </para>
/// GL-free and pure: the manager, the render pass and the tests all read the same values.
/// </summary>
/// <param name="VolumeSize">Edge length in cells of the volume the model fills.</param>
/// <param name="OpeningMinX">West edge of the opening.</param>
/// <param name="OpeningMaxX">East edge of the opening.</param>
/// <param name="OpeningFloorY">Top of the hearth floor the fire and the crucible stand on.</param>
/// <param name="OpeningCeilingY">Underside of what closes the opening above.</param>
/// <param name="OpeningDepthZ">Depth of the opening: its back wall, measured from the front face
/// at z = 0.</param>
/// <param name="CrucibleLocal">Center of the bowl a crucible rests on: on the hearth floor, or on whatever the
/// model raises it on (the furnace's pedestal).</param>
/// <param name="FireLocal">Where the flame burns, just above the floor and behind the bowl.</param>
/// <param name="FuelLocal">Center of the first fuel row on the floor.</param>
/// <param name="FuelPerRow">Fuel units lying side by side in one row, across the opening.</param>
/// <param name="FuelRowOffset">From one fuel row's center to the next row's.</param>
/// <param name="EmberMin">West and front corner (x, z) of the ember bed glowing on the floor of a lit hearth.</param>
/// <param name="EmberMax">East and back corner (x, z) of the ember bed.</param>
/// <param name="FlueLocal">Middle of the flue's mouth on top of the model, where a lit kiln smokes.</param>
public sealed record KilnLayout(
    int VolumeSize,
    float OpeningMinX,
    float OpeningMaxX,
    float OpeningFloorY,
    float OpeningCeilingY,
    float OpeningDepthZ,
    Vector3 CrucibleLocal,
    Vector3 FireLocal,
    Vector3 FuelLocal,
    int FuelPerRow,
    Vector3 FuelRowOffset,
    Vector2 EmberMin,
    Vector2 EmberMax,
    Vector3 FlueLocal)
{
    /// <summary>The mud brick kiln: a mouth x 8..24, y 4..19, z 0..20 between two piers, and the flue on top of
    /// its dome.</summary>
    public static readonly KilnLayout Kiln = new(
        VolumeSize: 2,
        OpeningMinX: 8f / 16f,
        OpeningMaxX: 24f / 16f,
        OpeningFloorY: 4f / 16f,
        OpeningCeilingY: 19f / 16f,
        OpeningDepthZ: 20f / 16f,
        // Toward the front so it can be reached; the bowl spans z 5..13, leaving the flame standing just behind
        // its back wall
        CrucibleLocal: new Vector3(1f, 4f / 16f, 9f / 16f),
        FireLocal: new Vector3(1f, 6f / 16f, 14.5f / 16f),
        // Far enough forward that a unit lying flat stays clear of the back wall, still behind the flame
        FuelLocal: new Vector3(1f, 4f / 16f, 17.5f / 16f),
        FuelPerRow: 4,
        FuelRowOffset: Vector3.Zero,
        EmberMin: new Vector2(8f / 16f, 13f / 16f),
        EmberMax: new Vector2(24f / 16f, 20f / 16f),
        FlueLocal: new Vector3(1f, 2f, 1f));

    /// <summary>The alloy furnace: a deep chamber x 10..38, y 5..24, z 0..36 between two walls, the crucible on
    /// a pedestal in its middle (top at y 8), a fuel row in front of the pedestal and one behind the flame, and
    /// the chimney on top of its roof.</summary>
    public static readonly KilnLayout Furnace = new(
        VolumeSize: 3,
        OpeningMinX: 10f / 16f,
        OpeningMaxX: 38f / 16f,
        OpeningFloorY: 5f / 16f,
        OpeningCeilingY: 24f / 16f,
        OpeningDepthZ: 36f / 16f,
        CrucibleLocal: new Vector3(1.5f, 8f / 16f, 1f),
        FireLocal: new Vector3(1.5f, 7f / 16f, 1.5f),
        FuelLocal: new Vector3(1.5f, 5f / 16f, 6.5f / 16f),
        FuelPerRow: 4,
        FuelRowOffset: new Vector3(0f, 0f, 1.45f),
        EmberMin: new Vector2(10f / 16f, 3f / 16f),
        EmberMax: new Vector2(38f / 16f, 36f / 16f),
        FlueLocal: new Vector3(1.5f, 3f, 1.5f));

    /// <summary>Every layout a kiln may have, smallest first.</summary>
    public static readonly KilnLayout[] All = [Kiln, Furnace];

    /// <summary>The layout of a kiln whose volume is <paramref name="size"/> cells along every
    /// edge, or null when no kiln model of that size exists.</summary>
    public static KilnLayout? ForSize(int size)
    {
        foreach (var layout in All)
            if (layout.VolumeSize == size)
                return layout;
        return null;
    }

    /// <summary>Bottom-center of the lump of solid charge waiting to melt: over the bowl, just
    /// above its rim. The render pass lifts by half the drawn size to center the mesh on it.</summary>
    public Vector3 PendingLocal =>
        new(CrucibleLocal.X, CrucibleLocal.Y + KilnRenderer.CrucibleTopY + KilnRenderer.PendingGap, CrucibleLocal.Z);

    /// <summary>Bottom-center of fuel unit <paramref name="index"/> (0-based) of <paramref name="count"/> stored
    /// units: rows of <see cref="FuelPerRow"/> filled front row first, each row centered on its own units, so a
    /// part-filled row still sits in the middle of the hearth.</summary>
    public Vector3 FuelSlotLocal(int index, int count)
    {
        var row = index / FuelPerRow;
        var inRow = Math.Min(FuelPerRow, count - row * FuelPerRow);
        var along = (index % FuelPerRow - (inRow - 1) * 0.5f) * KilnRenderer.FuelSpacing;
        return FuelLocal + FuelRowOffset * row + new Vector3(along, 0f, 0f);
    }

    /// <summary>Center of the ember bed, a hair over the hearth floor and under whatever lies on it.</summary>
    public Vector3 EmberLocal =>
        new((EmberMin.X + EmberMax.X) * 0.5f, OpeningFloorY + KilnRenderer.EmberLift, (EmberMin.Y + EmberMax.Y) * 0.5f);

    /// <summary>Extent of the ember bed across the opening (x) and into it (y).</summary>
    public Vector2 EmberSize => EmberMax - EmberMin;

    /// <summary>Half a block outside the opening's middle, at mid-height of the bottom cell: every
    /// cell inside the structure is solid brickwork, so the light the hearth sees is the light at
    /// its mouth.</summary>
    public Vector3 MouthLocal => new((OpeningMinX + OpeningMaxX) * 0.5f, 0.5f, -0.5f);

    /// <summary>Turns a local point of the volume into a world position for a kiln whose anchor
    /// cell (the volume's minimum corner) is <paramref name="anchor"/> and which faces
    /// <paramref name="facing"/>.</summary>
    public Vector3 ToWorld(Vector3 anchor, int facing, Vector3 local) =>
        anchor + MultiblockShape.RotatePoint(local, facing, VolumeSize);
}
