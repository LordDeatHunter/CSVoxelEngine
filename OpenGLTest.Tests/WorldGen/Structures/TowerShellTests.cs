using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Structures;

/// <summary>
/// The battle tower's shell geometry (<see cref="TowerShell"/>) over the smallest, a middle and the largest radius, the fewest
/// and the most floors and every quarter turn: the stair climbs half a block at a time from the ground to the roof, every
/// landing is level with its floor and opens onto its room, the door cells fill the tube two steps past their landing, closed
/// doors seal every floor from the next one and the top floor from the roof even with every breakable cell gone, the walls,
/// slabs and corridor are unbreakable bricks, the parapet is crenellated, and the rings nest outward by distance.
/// </summary>
public sealed class TowerShellTests
{
    private const int StepsPerTurn = WorldGenerationConfig.TowerStairStepsPerTurn;
    private const int Headroom = WorldGenerationConfig.TowerStairHeadroom;

    public TowerShellTests() => TestContent.EnsureLoaded();

    public static IEnumerable<object[]> Shapes()
    {
        int[] radii = [WorldGenerationConfig.TowerMinRadius, (WorldGenerationConfig.TowerMinRadius + WorldGenerationConfig.TowerMaxRadius) / 2,
            WorldGenerationConfig.TowerMaxRadius];
        int[] floors = [WorldGenerationConfig.TowerMinFloors, WorldGenerationConfig.TowerMaxFloors];
        foreach (var radius in radii)
        foreach (var floor in floors)
        for (var turns = 0; turns < 4; turns++)
            yield return [radius, floor, turns];
    }

    private static TowerSite SiteOf(int radius, int floors, int turns)
    {
        var floorTemplates = StructureRegistry.Of(StructureKind.TowerFloor);
        var altar = StructureRegistry.Of(StructureKind.TowerAltar)[0];
        Assert.NotEmpty(floorTemplates);
        var picks = new StructureTemplate[floors];
        var floorTurns = new int[floors];
        for (var k = 0; k < floors; k++)
        {
            picks[k] = floorTemplates[k % floorTemplates.Count];
            floorTurns[k] = k & 3;
        }
        // An off-origin center with a negative coordinate, so floor division and the world offsets are exercised
        return new TowerSite(3, -2, 4611, -2987, 72, radius, floors, turns, TowerPalette.For(Biome.Forest), altar, 1, picks, floorTurns);
    }

    /// <summary>Every column of the site's footprint box with its strip and ring.</summary>
    private static IEnumerable<(int Dx, int Dz, int Step, TowerRing Ring)> Columns(TowerSite s)
    {
        var r = s.OuterRadius;
        for (var dz = -r; dz <= r; dz++)
        for (var dx = -r; dx <= r; dx++)
        {
            var step = TowerShell.StepOf(s, dx, dz);
            yield return (dx, dz, step, TowerShell.RingOf(s, dx, dz, step));
        }
    }

    private static int BlockOf(int cell) => cell & 0xFF;

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheStairClimbsHalfABlockAtATimeFromTheGroundToTheRoof(int radius, int floors, int turns)
    {
        var s = SiteOf(radius, floors, turns);
        var corridor = Columns(s).Where(c => c.Ring == TowerRing.Corridor).ToList();
        for (var t = 0; t < s.StairSteps; t++)
        {
            var stepY = TowerShell.StepY(s, t);
            var block = TowerShell.IsSlabStep(t) ? s.Palette.Step : s.Palette.Brick;
            var strip = t % StepsPerTurn;
            var walkable = corridor.Any(c => c.Step == strip
                                             && BlockOf(TowerShell.CellAt(s, c.Ring, c.Step, stepY)) == block
                                             && Enumerable.Range(stepY + 1, Headroom)
                                                 .All(y => TowerShell.CellAt(s, c.Ring, c.Step, y) == BlockType.Air));
            Assert.True(walkable, $"half-step {t} has no corridor column with its step and headroom (r {radius}, {floors} floors, turn {turns})");
            if (t + 1 < s.StairSteps) Assert.InRange(TowerShell.StepY(s, t + 1) - stepY, 0, 1);
        }
        Assert.Equal(s.GroundY + 1, TowerShell.StepY(s, 0));
        Assert.Equal(s.RoofY, TowerShell.StepY(s, s.StairSteps - 1));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryLandingIsLevelWithItsFloorAndOpensOntoTheRoom(int radius, int floors, int turns)
    {
        var s = SiteOf(radius, floors, turns);
        var columns = Columns(s).ToList();
        for (var k = 1; k <= floors; k++) Assert.Equal(s.FloorY(k), TowerShell.StepY(s, s.LandingStep(k)));
        for (var k = 0; k < floors; k++)
        {
            var floorY = s.FloorY(k);
            var strip = k == 0 ? StepsPerTurn - 1 : s.LandingStep(k) % StepsPerTurn;
            var open = columns.Any(c => c.Ring == TowerRing.InnerWall && c.Step == strip
                                        && Enumerable.Range(floorY + 1, Headroom)
                                            .All(y => TowerShell.CellAt(s, c.Ring, c.Step, y) == BlockType.Air));
            Assert.True(open, $"floor {k} has no doorway in its inner wall (r {radius}, {floors} floors, turn {turns})");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void DoorCellsSpanTwoStepsPastTheirLanding(int radius, int floors, int turns)
    {
        var s = SiteOf(radius, floors, turns);
        for (var k = 0; k < floors; k++)
        {
            var cells = s.DoorCells(k);
            Assert.NotEmpty(cells);
            var first = s.DoorStep(k);
            Assert.True(first % 2 == 1, "a door must start on a full brick step");
            var landing = k == 0 ? 0 : s.LandingStep(k);
            Assert.True(first - landing > WorldGenerationConfig.TowerDoorwayHalfSteps, "a door must stand clear of its doorway");
            Assert.True(first + 1 < s.LandingStep(k + 1), "a door must end before the next landing");
            var strips = new HashSet<int>();
            foreach (var (x, y, z) in cells)
            {
                var dx = x - s.CenterX;
                var dz = z - s.CenterZ;
                var step = TowerShell.StepOf(s, dx, dz);
                Assert.Equal(TowerRing.Corridor, TowerShell.RingOf(s, dx, dz, step));
                Assert.True(step == first % StepsPerTurn || step == (first + 1) % StepsPerTurn, $"door cell in strip {step}");
                strips.Add(step);
                var t = step == first % StepsPerTurn ? first : first + 1;
                Assert.InRange(y, TowerShell.StepY(s, t) + 1, TowerShell.StepY(s, t) + Headroom);
                Assert.InRange(y, s.FloorY(k), s.FloorY(k + 1) + Headroom);
                Assert.Equal(BlockType.Air, TowerShell.CellAt(s, TowerRing.Corridor, step, y));
            }
            Assert.Equal(2, strips.Count);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ClosedDoorsSealEveryFloorEvenWithBreakableCellsRemoved(int radius, int floors, int turns)
    {
        var s = SiteOf(radius, floors, turns);
        var x0 = s.MinX - 1;
        var z0 = s.MinZ - 1;
        var y0 = s.BottomY - 1;
        var sx = s.MaxX + 2 - x0;
        var sz = s.MaxZ + 2 - z0;
        var sy = s.TopY + 2 - y0;
        int Index(int x, int y, int z) => ((y - y0) * sz + (z - z0)) * sx + (x - x0);

        // Solid: an unbreakable shell cell or a closed door. Breakable cells (slabs, the stoop), air, the dressing (ignored, it
        // is breakable) and everything the shell leaves alone are passable
        var solid = new bool[sx * sy * sz];
        for (var z = z0; z < z0 + sz; z++)
        for (var x = x0; x < x0 + sx; x++)
        {
            var dx = x - s.CenterX;
            var dz = z - s.CenterZ;
            var step = TowerShell.StepOf(s, dx, dz);
            var ring = TowerShell.RingOf(s, dx, dz, step);
            for (var y = y0; y < y0 + sy; y++)
            {
                var cell = TowerShell.CellAt(s, ring, step, y);
                solid[Index(x, y, z)] = cell > 0 && BlockType.IsUnbreakable((byte)BlockOf(cell));
            }
        }
        for (var k = 0; k < floors; k++)
            foreach (var (x, y, z) in s.DoorCells(k))
                solid[Index(x, y, z)] = true;

        var labels = Label(solid, sx, sy, sz);
        var outside = labels[Index(x0, y0, z0)];
        Assert.True(outside > 0);
        var roofTop = labels[Index(s.CenterX, s.RoofY + 1, s.CenterZ)];
        var rooms = new int[floors];
        for (var k = 0; k < floors; k++)
        {
            rooms[k] = labels[Index(s.CenterX, s.FloorY(k) + 1, s.CenterZ)];
            Assert.True(rooms[k] > 0, $"room {k}'s sample cell is solid");
        }
        Assert.Equal(outside, rooms[0]);
        Assert.Equal(floors, rooms.Distinct().Count());
        for (var k = 1; k < floors; k++)
        {
            Assert.NotEqual(outside, rooms[k]);
            Assert.NotEqual(roofTop, rooms[k]);
        }
    }

    // Face-connected component labels of the passable cells (0 = solid)
    private static int[] Label(bool[] solid, int sx, int sy, int sz)
    {
        var labels = new int[solid.Length];
        var queue = new Queue<int>();
        var next = 0;
        var layer = sx * sz;
        for (var start = 0; start < solid.Length; start++)
        {
            if (solid[start] || labels[start] != 0) continue;
            labels[start] = ++next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                var x = i % sx;
                var z = i / sx % sz;
                var y = i / layer;
                if (x > 0) Visit(i - 1);
                if (x < sx - 1) Visit(i + 1);
                if (z > 0) Visit(i - sx);
                if (z < sz - 1) Visit(i + sx);
                if (y > 0) Visit(i - layer);
                if (y < sy - 1) Visit(i + layer);
            }
        }
        return labels;

        void Visit(int j)
        {
            if (solid[j] || labels[j] != 0) return;
            labels[j] = next;
            queue.Enqueue(j);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheWallsSlabsAndCorridorAreUnbreakableBricks(int radius, int floors, int turns)
    {
        var s = SiteOf(radius, floors, turns);
        foreach (var palette in TowerPalette.All) Assert.True(BlockType.IsUnbreakable(palette.Brick));
        Assert.False(BlockType.IsUnbreakable(s.Palette.Step));
        foreach (var (_, _, step, ring) in Columns(s))
        {
            if (ring is TowerRing.Outside or TowerRing.Apron) continue;
            for (var y = s.BottomY; y <= s.GroundY; y++) AssertUnbreakable(TowerShell.CellAt(s, ring, step, y), $"{ring} foundation at {y}");
            if (ring == TowerRing.OuterWall)
            {
                for (var y = s.GroundY; y <= s.RoofY; y++)
                    if (!TowerShell.InEntrance(step) || y > s.GroundY + Headroom)
                        AssertUnbreakable(TowerShell.CellAt(s, ring, step, y), $"outer wall at {y}");
                var parapetTop = s.RoofY + WorldGenerationConfig.TowerParapetHeight;
                for (var y = s.RoofY + 1; y < parapetTop; y++) AssertUnbreakable(TowerShell.CellAt(s, ring, step, y), $"parapet at {y}");
                var crenel = TowerShell.CellAt(s, ring, step, parapetTop);
                Assert.Equal(step % 2 == 0 ? s.Palette.Brick : BlockType.Air, crenel);
            }
            else if (ring == TowerRing.Room)
            {
                for (var k = 1; k <= floors; k++) AssertUnbreakable(TowerShell.CellAt(s, ring, step, s.FloorY(k)), $"slab {k}");
            }
            else if (ring == TowerRing.Corridor)
            {
                for (var y = s.GroundY + 1; y <= s.RoofY; y++)
                {
                    var cell = TowerShell.CellAt(s, ring, step, y);
                    Assert.True(cell == BlockType.Air || cell == s.Palette.Step || BlockType.IsUnbreakable((byte)BlockOf(cell)),
                        $"corridor cell at {y} is {cell}");
                }
            }
        }
    }

    private static void AssertUnbreakable(int cell, string where) =>
        Assert.True(cell > 0 && BlockType.IsUnbreakable((byte)BlockOf(cell)), $"{where} is {cell}, not an unbreakable brick");

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheRingsNestOutwardByDistance(int radius, int floors, int turns)
    {
        var s = SiteOf(radius, floors, turns);
        var depth = new Dictionary<TowerRing, int>
        {
            [TowerRing.Room] = 0, [TowerRing.InnerWall] = 1, [TowerRing.Corridor] = 2,
            [TowerRing.OuterWall] = 3, [TowerRing.Apron] = 4, [TowerRing.Outside] = 5
        };
        var columns = Columns(s).ToList();
        Assert.All(columns.Where(c => c.Ring == TowerRing.Apron),
            c => Assert.True(TowerShell.InEntrance(c.Step), "an apron column outside the entrance strips"));
        foreach (var ring in Enum.GetValues<TowerRing>())
            Assert.Contains(columns, c => c.Ring == ring);

        // Strictly by squared distance: a nearer column is never in a ring farther out (apron and outside count as one band)
        var byDistance = columns.GroupBy(c => c.Dx * c.Dx + c.Dz * c.Dz).OrderBy(g => g.Key).ToList();
        var deepest = 0;
        foreach (var group in byDistance)
        {
            var ranks = group.Select(c => Math.Min(depth[c.Ring], depth[TowerRing.Apron])).ToList();
            Assert.True(ranks.Min() >= deepest, $"a ring nests inside a deeper one at distance² {group.Key}");
            Assert.Equal(ranks.Min(), ranks.Max());
            deepest = ranks.Max();
        }
    }
}
