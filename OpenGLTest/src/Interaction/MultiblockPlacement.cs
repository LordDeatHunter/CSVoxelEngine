using System;
using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free search for the cubic volume (<c>size</c> cells along every edge) a right-click meant,
/// shared by the converter item's world hook and the tests.
///
/// <para>
/// The clicked cell lies on the volume's front face, and the volume always extends <b>into</b>
/// the face that was hit (away from the player). Which of the volumes whose front face holds that
/// cell is taken is decided in screen terms, "right" being the player's screen-right when looking
/// at the face and "down" the opposite of screen-up: the volume reaching farthest right wins, and
/// among those the one reaching farthest down. For a 2x2x2 that is the order right-down,
/// right-up, left-down, left-up over the four volumes the clicked corner belongs to. The first
/// candidate whose cells are all the source block wins, so building a neat cube and clicking any
/// of its front cells always converts that cube.
/// </para>
/// <para>
/// The structure's opening ends up facing the player: on a side face that is the hit normal
/// itself, and on a top or bottom face (where the normal says nothing about where the player
/// stands) it is the player's own horizontal view direction, reversed.
/// </para>
/// </summary>
public static class MultiblockPlacement
{
    /// <summary>
    /// Finds the <paramref name="size"/>-cell volume a click on <paramref name="clickedCell"/>'s
    /// <paramref name="hitNormal"/> face means, reading the world through
    /// <paramref name="blockAt"/> (an unloaded cell must read as air, i.e. never
    /// <paramref name="sourceId"/>). Returns the volume's minimum corner as
    /// <paramref name="anchor"/> and the facing of the structure's front (an engine face index
    /// 0..3), or false when no candidate is built entirely from <paramref name="sourceId"/>.
    /// </summary>
    public static bool TryFindVolume(Func<int, int, int, byte> blockAt, byte sourceId, int size,
        Vector3 clickedCell, Vector3 hitNormal, Vector3 viewForward, out Vector3 anchor, out int facing)
    {
        anchor = Vector3.Zero;

        var normal = SnapToAxis(hitNormal);
        Vector3 screenUp;
        if (MathF.Abs(normal.Y) < 0.5f)
        {
            // A side face: the player looks at it along the normal, so screen-up is world up and
            // the opening faces straight back out of the face that was clicked.
            screenUp = Vector3.UnitY;
            facing = MultiblockShape.FacingOf(normal);
        }
        else
        {
            // A top or bottom face says nothing about where the player stands, so the view
            // direction supplies both the screen axis and the side the opening faces.
            var horizontal = SnapHorizontal(viewForward);
            screenUp = horizontal;
            facing = MultiblockShape.FacingOf(-horizontal);
        }

        if (facing < 0 || !MultiblockShape.IsValidSize(size)) return false;

        var right = Vector3.Cross(screenUp, normal);
        var depth = -normal;
        var (clickX, clickY, clickZ) = VoxelCoords.BlockCellOf(clickedCell);

        // A candidate's front face spans right steps rightStart..rightStart + size - 1 and up
        // steps upStart..upStart + size - 1 from the clicked cell: rightmost first, then lowest
        for (var rightStart = 0; rightStart > -size; rightStart--)
        for (var upStart = 1 - size; upStart <= 0; upStart++)
        {
            if (TryCandidate(blockAt, sourceId, size, clickX, clickY, clickZ,
                    right * rightStart, screenUp * upStart, right, screenUp, depth, out anchor))
                return true;
        }

        facing = -1;
        return false;
    }

    /// <summary>Whether the candidate volume whose front face starts at the clicked cell moved
    /// by <paramref name="rightOrigin"/> + <paramref name="upOrigin"/> is all source block; its
    /// minimum corner on every axis (whatever the facing) is the anchor.</summary>
    private static bool TryCandidate(Func<int, int, int, byte> blockAt, byte sourceId, int size,
        int clickX, int clickY, int clickZ, Vector3 rightOrigin, Vector3 upOrigin,
        Vector3 right, Vector3 up, Vector3 depth, out Vector3 anchor)
    {
        anchor = Vector3.Zero;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var minZ = int.MaxValue;

        for (var a = 0; a < size; a++)
        for (var b = 0; b < size; b++)
        for (var k = 0; k < size; k++)
        {
            var offset = rightOrigin + upOrigin + right * a + up * b + depth * k;
            var x = clickX + (int)MathF.Round(offset.X);
            var y = clickY + (int)MathF.Round(offset.Y);
            var z = clickZ + (int)MathF.Round(offset.Z);

            if (blockAt(x, y, z) != sourceId) return false;

            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
        }

        anchor = new Vector3(minX, minY, minZ);
        return true;
    }

    /// <summary>The unit axis a vector points most strongly along.</summary>
    private static Vector3 SnapToAxis(Vector3 v)
    {
        var ax = MathF.Abs(v.X);
        var ay = MathF.Abs(v.Y);
        var az = MathF.Abs(v.Z);
        if (ax >= ay && ax >= az) return new Vector3(MathF.Sign(v.X), 0f, 0f);
        if (ay >= az) return new Vector3(0f, MathF.Sign(v.Y), 0f);
        return new Vector3(0f, 0f, MathF.Sign(v.Z));
    }

    /// <summary>The horizontal unit axis a view direction points most strongly along; zero when
    /// it is perfectly vertical, which leaves the caller without a facing.</summary>
    private static Vector3 SnapHorizontal(Vector3 v)
    {
        var ax = MathF.Abs(v.X);
        var az = MathF.Abs(v.Z);
        return ax >= az
            ? new Vector3(MathF.Sign(v.X), 0f, 0f)
            : new Vector3(0f, 0f, MathF.Sign(v.Z));
    }
}
