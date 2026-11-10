using System;
using System.Collections.Generic;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// The battle tower's shell as pure geometry, GL-free: a column's ring (<see cref="RingOf"/>) by its integer squared distance
/// from the center and its stair strip (<see cref="StepOf"/>) by its angle, and the cell at any height (<see cref="CellAt"/>).
/// <para>
/// From outside in: an outer wall <see cref="WorldGenerationConfig.TowerWallThickness"/> thick, crenellated over the roof and
/// cut by the ground entrance; a stair corridor, solid brick but for a helical tube that climbs half a block per angular strip
/// (<see cref="WorldGenerationConfig.TowerStairStepsPerTurn"/> strips a turn), its half-steps alternating the palette's slab
/// (even) and brick (odd); an inner wall with one doorway per floor where the stair is level with that floor's slab; and the
/// rooms, a slab every <see cref="WorldGenerationConfig.TowerFloorHeight"/> blocks up to the roof. A short apron cuts a walkway
/// in front of the entrance. Over the roof the generator closes the parapet's top with the forcefield dome
/// (<see cref="TowerDome"/>), which is not part of the shell. Every disc cell from the foundation to the top is written, as air or block. The barrier door of
/// floor k (BT-4) fills the tube over two steps just past its doorway (<see cref="DoorCells"/>, <see cref="IsDoorStep"/>):
/// worldgen stamps the closed door there as the unbreakable door block, which the BT-4 <c>TowerManager</c> opens to air once the
/// floor is cleared. With those cells shut, the unbreakable bricks seal every floor from the next one and the top floor from the
/// roof.
/// </para>
/// </summary>
internal static class TowerShell
{
    private const int StepsPerTurn = WorldGenerationConfig.TowerStairStepsPerTurn;
    private const int Headroom = WorldGenerationConfig.TowerStairHeadroom;

    static TowerShell()
    {
        Require(StepsPerTurn / 2 > Headroom + 1,
            "TowerStairStepsPerTurn / 2 must exceed TowerStairHeadroom + 1, or one turn of the stair tube runs into the next.");
        Require(WorldGenerationConfig.TowerDoorStepOffset % 2 == 0
                && WorldGenerationConfig.TowerDoorStepOffset > WorldGenerationConfig.TowerDoorwayHalfSteps + 2,
            "TowerDoorStepOffset must be even and exceed TowerDoorwayHalfSteps + 2, so a door is a brick step clear of the doorway.");
        Require(2 * WorldGenerationConfig.TowerFloorHeight > WorldGenerationConfig.TowerDoorStepOffset + 1,
            "2 * TowerFloorHeight must exceed TowerDoorStepOffset + 1, so a door's two steps end before the next landing.");
        Require(WorldGenerationConfig.TowerMinRadius - WorldGenerationConfig.TowerWallThickness - WorldGenerationConfig.TowerStairWidth
                - WorldGenerationConfig.TowerInnerWallThickness >= 12,
            "The smallest tower's room must reach radius 12, or a 17-wide dressing template does not fit in it.");
        Require(WorldGenerationConfig.TowerCellBlocks > 2 * (WorldGenerationConfig.TowerMaxRadius + WorldGenerationConfig.TowerApronDepth
                + WorldGenerationConfig.TowerSiteWaterRing + 1),
            "TowerCellBlocks must exceed twice a tower's reach plus its site ring, or a footprint cannot stay inside its cell.");
    }

    // A method, not an if on constants, so a constant condition never reads as unreachable code
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>The stair strip (0..steps per turn - 1) of the column at (<paramref name="dx"/>, <paramref name="dz"/>) from the
    /// center: its angle, turned by the site's quarter turns.</summary>
    internal static int StepOf(TowerSite s, int dx, int dz)
    {
        var turn = MathF.Atan2(dz, dx) / (2 * MathF.PI) + 1 + s.Turns * 0.25f;
        var phase = turn - MathF.Floor(turn);
        return Math.Min(StepsPerTurn - 1, (int)(phase * StepsPerTurn));
    }

    /// <summary>Whether strip <paramref name="step"/> is one of the entrance strips, the last of a turn, just before the stair's
    /// first step.</summary>
    internal static bool InEntrance(int step) => step >= StepsPerTurn - WorldGenerationConfig.TowerEntranceSteps;

    /// <summary>The ring of the column at (<paramref name="dx"/>, <paramref name="dz"/>) from the center, in strip
    /// <paramref name="step"/>; a ring of radius r holds the columns with dx² + dz² &lt;= r² + r.</summary>
    internal static TowerRing RingOf(TowerSite s, int dx, int dz, int step)
    {
        var d2 = dx * dx + dz * dz;
        if (d2 > Edge(s.OuterRadius)) return TowerRing.Outside;
        if (d2 > Edge(s.Radius)) return InEntrance(step) ? TowerRing.Apron : TowerRing.Outside;
        if (d2 > Edge(s.CorridorRadius)) return TowerRing.OuterWall;
        if (d2 > Edge(s.InnerWallRadius)) return TowerRing.Corridor;
        return d2 > Edge(s.RoomRadius) ? TowerRing.InnerWall : TowerRing.Room;
    }

    private static int Edge(int r) => r * r + r;

    /// <summary>The world y of half-step <paramref name="t"/>'s block: the ground slab for the entrance's negative steps, then
    /// half a block up per step.</summary>
    internal static int StepY(TowerSite s, int t) => t < 0 ? s.GroundY : s.GroundY + 1 + t / 2;

    /// <summary>Whether half-step <paramref name="t"/> is a slab (even) rather than a full brick (odd).</summary>
    internal static bool IsSlabStep(int t) => t >= 0 && t % 2 == 0;

    /// <summary>The shell cell at world height <paramref name="y"/> of a column in <paramref name="ring"/> and strip
    /// <paramref name="step"/>: -1 (write nothing) or <c>block | state &lt;&lt; 8</c>, air included.</summary>
    internal static int CellAt(TowerSite s, TowerRing ring, int step, int y)
    {
        if (ring == TowerRing.Outside || y < s.BottomY || y > s.TopY) return -1;
        if (ring == TowerRing.Apron)
        {
            if (y <= s.GroundY) return TowerPalette.StoopBlock;
            return y <= s.GroundY + Headroom ? BlockType.Air : -1;
        }
        if (y <= s.GroundY) return s.Palette.Brick;
        return ring switch
        {
            TowerRing.OuterWall => OuterWallAt(s, step, y),
            TowerRing.Corridor => CorridorAt(s, step, y),
            TowerRing.InnerWall => InnerWallAt(s, step, y),
            _ => RoomAt(s, y)
        };
    }

    private static int OuterWallAt(TowerSite s, int step, int y)
    {
        var parapetTop = s.RoofY + WorldGenerationConfig.TowerParapetHeight;
        if (y < parapetTop) return InEntrance(step) && y <= s.GroundY + Headroom ? BlockType.Air : s.Palette.Brick;
        if (y == parapetTop) return step % 2 == 0 ? s.Palette.Brick : BlockType.Air;
        return BlockType.Air;
    }

    private static int CorridorAt(TowerSite s, int step, int y)
    {
        var turns = s.StairSteps / StepsPerTurn + 1;
        for (var m = -1; m <= turns; m++)
        {
            var t = step + StepsPerTurn * m;
            if (t >= s.StairSteps) break;
            if (t < 0 && !(m == -1 && InEntrance(step))) continue;
            var stepY = StepY(s, t);
            if (y == stepY) return IsSlabStep(t) ? s.Palette.Step : s.Palette.Brick;
            if (y > stepY && y <= stepY + Headroom) return IsDoorStep(s, t) ? TowerPalette.DoorBlock : BlockType.Air;
        }
        return y <= s.RoofY ? s.Palette.Brick : BlockType.Air;
    }

    /// <summary>Whether half-step <paramref name="t"/> is one of the two steps a floor's closed barrier door fills the tube
    /// over: <see cref="TowerSite.DoorStep"/> of some floor k (0..<see cref="TowerSite.Floors"/> - 1) or the step after it.
    /// Closed-form (it mirrors <c>DoorStep(k) = (k == 0 ? 1 : 2kH - 1) + offset</c>), so the stamp's hot path runs no loop.</summary>
    internal static bool IsDoorStep(TowerSite s, int t)
    {
        var u = t - WorldGenerationConfig.TowerDoorStepOffset;
        if (u < 0) return false;
        if (u == 1 || u == 2) return true;
        const int period = 2 * WorldGenerationConfig.TowerFloorHeight;
        var k = (u + 1) / period;
        var r = (u + 1) % period;
        return (r == 0 || r == 1) && k >= 1 && k < s.Floors;
    }

    private static int InnerWallAt(TowerSite s, int step, int y)
    {
        if (y > s.RoofY) return BlockType.Air;
        if (y <= s.GroundY + Headroom) return InEntrance(step) ? BlockType.Air : s.Palette.Brick;
        var k = (y - s.GroundY - 1) / WorldGenerationConfig.TowerFloorHeight;
        if (k < 1 || k >= s.Floors || y > s.FloorY(k) + Headroom) return s.Palette.Brick;
        var gap = Math.Abs(step - Mod(s.LandingStep(k), StepsPerTurn));
        gap = Math.Min(gap, StepsPerTurn - gap);
        if (gap > WorldGenerationConfig.TowerDoorwayHalfSteps) return s.Palette.Brick;
        // A lantern hangs from the lintel at the middle of each doorway
        return gap == 0 && y == s.FloorY(k) + Headroom ? TowerPalette.LanternBlock : BlockType.Air;
    }

    private static int RoomAt(TowerSite s, int y)
    {
        var above = y - s.GroundY;
        return above % WorldGenerationConfig.TowerFloorHeight == 0 && above / WorldGenerationConfig.TowerFloorHeight <= s.Floors
            ? s.Palette.Brick
            : BlockType.Air;
    }

    private static int Mod(int v, int m) => (v % m + m) % m;

    /// <summary>The stair tube cells floor <paramref name="k"/>'s barrier door fills: in every corridor column of the door's two
    /// strips, the headroom over that column's step. Worldgen stamps the closed door there as the door block, and the BT-4
    /// <c>TowerManager</c> opens it to air.</summary>
    internal static IReadOnlyList<(int X, int Y, int Z)> DoorCells(TowerSite s, int k)
    {
        if (k < 0 || k >= s.Floors) throw new ArgumentOutOfRangeException(nameof(k), k, "A tower has a door per floor below the roof.");
        var first = s.DoorStep(k);
        var cells = new List<(int X, int Y, int Z)>();
        var r = s.CorridorRadius;
        for (var dz = -r; dz <= r; dz++)
        for (var dx = -r; dx <= r; dx++)
        {
            var step = StepOf(s, dx, dz);
            if (RingOf(s, dx, dz, step) != TowerRing.Corridor) continue;
            int t;
            if (step == Mod(first, StepsPerTurn)) t = first;
            else if (step == Mod(first + 1, StepsPerTurn)) t = first + 1;
            else continue;
            var stepY = StepY(s, t);
            for (var y = stepY + 1; y <= stepY + Headroom; y++) cells.Add((s.CenterX + dx, y, s.CenterZ + dz));
        }
        return cells;
    }
}
