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
/// <param name="CrucibleLocal">Center of the bowl a crucible rests on, on the hearth floor.</param>
/// <param name="FireLocal">Where the flame burns, just above the floor and behind the bowl.</param>
/// <param name="FuelLocal">Center of the fuel row on the floor, behind the flame.</param>
public sealed record KilnLayout(
    int VolumeSize,
    float OpeningMinX,
    float OpeningMaxX,
    float OpeningFloorY,
    float OpeningCeilingY,
    float OpeningDepthZ,
    Vector3 CrucibleLocal,
    Vector3 FireLocal,
    Vector3 FuelLocal)
{
    /// <summary>The mud brick kiln: an opening x 8..24, y 2..18, z 0..12 between two piers.</summary>
    public static readonly KilnLayout Kiln = new(
        VolumeSize: 2,
        OpeningMinX: 8f / 16f,
        OpeningMaxX: 24f / 16f,
        OpeningFloorY: 2f / 16f,
        OpeningCeilingY: 18f / 16f,
        OpeningDepthZ: 12f / 16f,
        // Toward the front so it can be reached; the bowl spans z 0.03..0.53, leaving the flame
        // standing just behind its back wall
        CrucibleLocal: new Vector3(1f, 2f / 16f, 0.28f),
        FireLocal: new Vector3(1f, 0.25f, 0.55f),
        // Far enough forward that a unit lying flat stays clear of the back wall, still behind
        // the flame
        FuelLocal: new Vector3(1f, 2f / 16f, 0.62f));

    /// <summary>The alloy furnace: a deep chamber x 8..40, y 4..30, z 0..36 between two walls,
    /// wide enough for its eight fuel units in one row.</summary>
    public static readonly KilnLayout Furnace = new(
        VolumeSize: 3,
        OpeningMinX: 8f / 16f,
        OpeningMaxX: 40f / 16f,
        OpeningFloorY: 4f / 16f,
        OpeningCeilingY: 30f / 16f,
        OpeningDepthZ: 36f / 16f,
        CrucibleLocal: new Vector3(1.5f, 4f / 16f, 0.7f),
        FireLocal: new Vector3(1.5f, 6f / 16f, 1.05f),
        FuelLocal: new Vector3(1.5f, 4f / 16f, 1.25f));

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
        new(CrucibleLocal.X, OpeningFloorY + KilnRenderer.CrucibleTopY + KilnRenderer.PendingGap, CrucibleLocal.Z);

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
