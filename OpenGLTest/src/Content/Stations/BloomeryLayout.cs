using System;
using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// Authoring contract of the bloomery model (<c>assets/models/block/bloomery.json</c>, 2x2x2, 0..32 model units):
/// where its tapping arch and its open throat are, and where the things it holds lie in them. Every number mirrors
/// the model, authored with the arch on north (-Z), so these are cell-space values in the volume's local frame and
/// <see cref="ToWorld"/> turns one onto the bloomery's actual facing. The fuel burns on the hearth inside the arch,
/// the finished blooms wait at the arch's front, and the ore charge lies in the throat on top of the stack.
/// GL-free and pure: the manager, the render pass, the particles and the tests all read the same values.
/// </summary>
public static class BloomeryLayout
{
    /// <summary>Edge length in cells of the volume the model fills.</summary>
    public const int VolumeSize = 2;

    /// <summary>West edge of the tapping arch.</summary>
    public const float ArchMinX = 10f / 16f;

    /// <summary>East edge of the tapping arch.</summary>
    public const float ArchMaxX = 22f / 16f;

    /// <summary>Top of the hearth floor inside the arch.</summary>
    public const float ArchFloorY = 5f / 16f;

    /// <summary>Underside of the lintel over the arch.</summary>
    public const float ArchCeilingY = 12f / 16f;

    /// <summary>Front of the arch: the face of the hearth tier.</summary>
    public const float ArchFrontZ = 3f / 16f;

    /// <summary>Back wall of the arch.</summary>
    public const float ArchBackZ = 15f / 16f;

    /// <summary>West and north edge of the throat well (it is square and centered).</summary>
    public const float ThroatMin = 11f / 16f;

    /// <summary>East and south edge of the throat well.</summary>
    public const float ThroatMax = 21f / 16f;

    /// <summary>Top of the throat's floor, where the ore charge lies.</summary>
    public const float ThroatFloorY = 26.25f / 16f;

    /// <summary>Fuel units lying side by side in one row of the bed.</summary>
    public const int FuelPerRow = 2;

    /// <summary>Center of the fuel bed's front row, on the hearth floor behind the blooms.</summary>
    public static readonly Vector3 FuelLocal = new(1f, ArchFloorY, 9f / 16f);

    /// <summary>Where the finished blooms pile up: on the hearth floor at the arch's front, ready to be taken.</summary>
    public static readonly Vector3 BloomLocal = new(1f, ArchFloorY, 5f / 16f);

    /// <summary>Where the ore charge lies: the middle of the throat's floor.</summary>
    public static readonly Vector3 ChargeLocal = new(1f, ThroatFloorY, 1f);

    /// <summary>Where the flame burns: over the fuel bed, under the lintel.</summary>
    public static readonly Vector3 FireLocal = new(1f, 7f / 16f, 10f / 16f);

    /// <summary>Middle of the throat's mouth on top of the stack: where a lit bloomery smokes and a bellows blast
    /// throws its sparks.</summary>
    public static readonly Vector3 ThroatLocal = new(1f, 2f, 1f);

    /// <summary>Half a block outside the arch's middle, at mid-height of the bottom cell: the light the hearth
    /// sees is the light at its mouth.</summary>
    public static readonly Vector3 MouthLocal = new(1f, 0.5f, -0.5f);

    /// <summary>Center of the ember bed glowing on the hearth floor of a lit bloomery.</summary>
    public static readonly Vector3 HearthEmberLocal =
        new((ArchMinX + ArchMaxX) * 0.5f, ArchFloorY + KilnRenderer.EmberLift, (ArchFrontZ + ArchBackZ) * 0.5f);

    /// <summary>Extent of the hearth's ember bed across the arch (x) and into it (y).</summary>
    public static readonly Vector2 HearthEmberSize = new(ArchMaxX - ArchMinX, ArchBackZ - ArchFrontZ);

    /// <summary>Center of the glow on the throat's floor, under the charge.</summary>
    public static readonly Vector3 ThroatEmberLocal = new(1f, ThroatFloorY + KilnRenderer.EmberLift, 1f);

    /// <summary>Edge length of the throat's square glow.</summary>
    public const float ThroatEmberSize = ThroatMax - ThroatMin;

    /// <summary>Bottom-center of fuel unit <paramref name="index"/> (0-based) of <paramref name="count"/> stored
    /// units: rows of <see cref="FuelPerRow"/> filled front row first, each row centered on its own units.</summary>
    public static Vector3 FuelSlotLocal(int index, int count)
    {
        var row = index / FuelPerRow;
        var inRow = Math.Min(FuelPerRow, count - row * FuelPerRow);
        var along = (index % FuelPerRow - (inRow - 1) * 0.5f) * KilnRenderer.FuelSpacing;
        return FuelLocal + new Vector3(along, 0f, row * KilnRenderer.FuelSpacing);
    }

    /// <summary>
    /// The ore charge as a stack of pieces in the throat: <c>Count</c> pieces, all full size except the top one,
    /// drawn at <c>TopScale</c> (0..1): the unit being smelted, which shrinks as its
    /// <paramref name="unitProgress01"/> runs up and is gone exactly when it turns into a bloom, so the charge
    /// sinks without a jump (and grows back when a cold fire runs the progress back).
    /// </summary>
    public static (int Count, float TopScale) ChargeStack(int ore, float unitProgress01) =>
        ore <= 0 ? (0, 0f) : (ore, 1f - MathUtils.Clamp01(unitProgress01));

    /// <summary>Turns a local point of the volume into a world position for a bloomery whose anchor cell (the
    /// volume's minimum corner) is <paramref name="anchor"/> and which faces <paramref name="facing"/>.</summary>
    public static Vector3 ToWorld(Vector3 anchor, int facing, Vector3 local) =>
        anchor + MultiblockShape.RotatePoint(local, facing, VolumeSize);
}
