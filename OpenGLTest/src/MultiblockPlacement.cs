using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free search for the 2x2x2 volume a right-click meant, shared by the converter item's world
/// hook and the tests.
///
/// <para>
/// The clicked cell is one corner of the volume, which always extends <b>into</b> the face that
/// was hit (away from the player). Which of the four volumes containing that corner is taken is
/// decided in screen terms, in the order right-down, right-up, left-down, left-up: "right" is the
/// player's screen-right when looking at the face, "down" the opposite of screen-up. The first
/// candidate whose eight cells are all the source block wins, so building a neat 2x2x2 and
/// clicking any of its front cells always converts that cube.
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
    /// The four candidate volumes as (right, up) signs in screen space, tried in this order:
    /// right-down, right-up, left-down, left-up.
    /// </summary>
    public static readonly (int Right, int Up)[] CandidateOrder = [(1, -1), (1, 1), (-1, -1), (-1, 1)];

    /// <summary>
    /// Finds the volume a click on <paramref name="clickedCell"/>'s
    /// <paramref name="hitNormal"/> face means, reading the world through
    /// <paramref name="blockAt"/> (an unloaded cell must read as air, i.e. never
    /// <paramref name="sourceId"/>). Returns the volume's minimum corner as
    /// <paramref name="anchor"/> and the facing of the structure's front (an engine face index
    /// 0..3), or false when none of the four candidates is built entirely from
    /// <paramref name="sourceId"/>.
    /// </summary>
    public static bool TryFindVolume(Func<int, int, int, byte> blockAt, byte sourceId, Vector3 clickedCell,
        Vector3 hitNormal, Vector3 viewForward, out Vector3 anchor, out int facing)
    {
        anchor = Vector3.Zero;
        facing = -1;

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

        if (facing < 0) return false;

        var right = Vector3.Cross(screenUp, normal);
        var depth = -normal;
        var (clickX, clickY, clickZ) = VoxelCoords.BlockCellOf(clickedCell);

        foreach (var (signRight, signUp) in CandidateOrder)
        {
            var step = right * signRight;
            var up = screenUp * signUp;

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var minZ = int.MaxValue;
            var fits = true;

            for (var a = 0; a < Multiblock.Size && fits; a++)
            for (var b = 0; b < Multiblock.Size && fits; b++)
            for (var k = 0; k < Multiblock.Size && fits; k++)
            {
                var offset = step * a + up * b + depth * k;
                var x = clickX + (int)MathF.Round(offset.X);
                var y = clickY + (int)MathF.Round(offset.Y);
                var z = clickZ + (int)MathF.Round(offset.Z);

                if (blockAt(x, y, z) != sourceId)
                {
                    fits = false;
                    break;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
            }

            if (!fits) continue;

            // The anchor is the volume's minimum corner on every axis, whatever the facing
            anchor = new Vector3(minX, minY, minZ);
            return true;
        }

        facing = -1;
        return false;
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
