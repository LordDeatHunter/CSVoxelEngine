using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The pane renderer's dimensions as block data (<see cref="PaneProfile"/>): every pane shape's
/// boxes stay in the cell, never overlap and reach the cell edge exactly toward its arms; panes
/// join only panes of their own shape (a fence never reaches into a glass pane); a pane that
/// declares nothing keeps the glass pane's shape; and a shape whose arms leave the post showing
/// (a fence's rails, a wall's narrower arms) is drawn exactly as its boxes.
/// </summary>
public sealed class PaneProfileTests
{
    private const int FloorY = 4;

    public PaneProfileTests() => TestContent.EnsureLoaded();

    private static BlockType[] Panes() =>
        BlockType.Types.Values.Where(t => t.Renderer == BlockRenderer.Pane).ToArray();

    private static float Overlap(BlockHitbox a, BlockHitbox b)
    {
        var min = Vector3.Max(a.Min, b.Min);
        var max = Vector3.Min(a.Max, b.Max);
        var size = Vector3.Max(max - min, Vector3.Zero);
        return size.X * size.Y * size.Z;
    }

    [Fact]
    public void EveryPaneShapesBoxesStayInTheCellApartAndReachTheEdgeTowardEachArm()
    {
        foreach (var type in Panes())
        {
            var profile = type.Pane!;
            Assert.InRange(profile.ArmFar - profile.ArmNear, 0f, profile.PostFar - profile.PostNear + 1e-6f);
            for (var mask = 0; mask < 16; mask++)
            {
                var boxes = profile.Boxes(mask);
                foreach (var box in boxes)
                    Assert.True(Vector3.Min(box.Min, Vector3.Zero) == Vector3.Zero && Vector3.Max(box.Max, Vector3.One) == Vector3.One,
                        $"{type.Name} mask {mask}: box {box} leaves the cell");
                for (var i = 0; i < boxes.Length; i++)
                for (var j = i + 1; j < boxes.Length; j++)
                    Assert.True(Overlap(boxes[i], boxes[j]) < 1e-6f, $"{type.Name} mask {mask}: boxes {i} and {j} overlap");

                var min = boxes.Select(b => b.Min).Aggregate(Vector3.Min);
                var max = boxes.Select(b => b.Max).Aggregate(Vector3.Max);
                Assert.Equal((mask & 4) != 0 ? 0f : profile.PostNear, min.X);
                Assert.Equal((mask & 8) != 0 ? 1f : profile.PostFar, max.X);
                Assert.Equal((mask & 1) != 0 ? 0f : profile.PostNear, min.Z);
                Assert.Equal((mask & 2) != 0 ? 1f : profile.PostFar, max.Z);
                // The post always stands the full height
                Assert.Equal((0f, 1f), (min.Y, max.Y));
            }
        }
    }

    [Fact]
    public void PanesJoinOnlyPanesOfTheirOwnShape()
    {
        var shapes = Panes().GroupBy(t => t.Pane!).Select(g => g.First()).ToArray();
        Assert.True(shapes.Length >= 2, "needs two pane shapes to compare");
        var stone = ContentRegistry.RequireBlock("stone").Id;

        foreach (var self in shapes)
        {
            Assert.True(PaneShape.ConnectsTo(self.Pane!, self.Id, 0), $"{self.Name} does not join its own kind");
            Assert.True(PaneShape.ConnectsTo(self.Pane!, stone, 0), $"{self.Name} does not join a solid full cube");
            foreach (var other in shapes.Where(o => o.Pane != self.Pane))
                Assert.False(PaneShape.ConnectsTo(self.Pane!, other.Id, 0), $"{self.Name} reaches into {other.Name}");
        }
    }

    [Fact]
    public void APaneWithoutDimensionsKeepsTheDefaultShapeAndEqualShapesAreOne()
    {
        const string path = "data/blocks/x.json";
        Assert.Same(PaneProfile.Default, ContentRegistry.ParsePaneProfile(null, path));
        Assert.Same(PaneProfile.Default, ContentRegistry.ParsePaneProfile(new BlockDefinition.PaneDefinition(), path));
        Assert.True(PaneProfile.Default.FullArms);

        var rails = new[] { (5, 7), (11, 13) };
        Assert.Same(PaneProfile.Create(5, 3, rails), PaneProfile.Create(5, 3, rails));
        Assert.NotSame(PaneProfile.Create(5, 3, rails), PaneProfile.Create(5, 1, rails));
        // An arm left out is as wide as the post
        Assert.Equal(7, ContentRegistry.ParsePaneProfile(new BlockDefinition.PaneDefinition { Post = 7 }, path).ArmPx);
    }

    [Fact]
    public void TheRegistryRefusesAPaneShapeItCannotDraw()
    {
        const string path = "data/blocks/x.json";
        foreach (var definition in new[]
                 {
                     new BlockDefinition.PaneDefinition { Post = 0 },
                     new BlockDefinition.PaneDefinition { Post = 17 },
                     new BlockDefinition.PaneDefinition { Post = 4, Arm = 6 },
                     new BlockDefinition.PaneDefinition { Rails = [] },
                     new BlockDefinition.PaneDefinition { Rails = [[4, 2]] },
                     new BlockDefinition.PaneDefinition { Rails = [[0, 17]] },
                     new BlockDefinition.PaneDefinition { Rails = [[2, 8], [6, 12]] },
                     new BlockDefinition.PaneDefinition { Rails = [[2, 8, 9]] },
                 })
            Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
                ContentRegistry.ParsePaneProfile(definition, path)).Message);
    }

    [Fact]
    public void StackedPanesOfOneKindDrawNoFaceWhereTheyMeet()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var fused = Panes().Where(t => t.CullSelf).ToArray();
        Assert.NotEmpty(fused);

        foreach (var type in fused)
        {
            var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
            var y = FloorY + 1;
            // Two of them stacked, both with an arm into a stone column to the west
            center.SetBlockRaw(8, y, 8, type.Id, 0);
            center.SetBlockRaw(8, y + 1, 8, type.Id, 0);
            center.SetBlockRaw(7, y, 8, stone, 0);
            center.SetBlockRaw(7, y + 1, 8, stone, 0);
            var mesh = center.GenerateMeshData(manager);
            Assert.NotNull(mesh);
            var list = type.Translucent ? mesh!.TranslucentVertices : mesh!.Vertices;

            Assert.DoesNotContain(Quads(list), q =>
                q.All(v => v.Layer == type.Top || v.Layer == type.Bottom || v.Layer == type.Side)
                && q.All(v => MathF.Abs(v.Position.Y - (y + 1)) < 1e-4f)
                && q.Average(v => v.Position.X) is var cx && cx > 7.5f && cx < 9f
                && q.Average(v => v.Position.Z) is var cz && cz > 8f && cz < 9f);
        }
    }

    private static IEnumerable<Vertex[]> Quads(List<Vertex> vertices)
    {
        for (var q = 0; q + 3 < vertices.Count; q += 4)
            yield return [vertices[q], vertices[q + 1], vertices[q + 2], vertices[q + 3]];
    }

    [Fact]
    public void APaneWhoseArmsLeaveItsPostShowingIsDrawnExactlyAsItsBoxes()
    {
        // A fence's rails, a wall's narrower arms: the post's sides stay, the arms keep their caps
        var railed = Panes().Where(t => !t.Pane!.FullArms).ToArray();
        Assert.NotEmpty(railed);
        var stone = ContentRegistry.RequireBlock("stone").Id;

        foreach (var type in railed)
        {
            var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
            var y = FloorY + 1;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                center.SetBlockRaw(x, FloorY, z, stone, 0);
            // Arms toward a neighbor of its own kind (west) and a stone block (north)
            center.SetBlockRaw(8, y, 8, type.Id, 0);
            center.SetBlockRaw(7, y, 8, type.Id, 0);
            center.SetBlockRaw(8, y, 7, stone, 0);
            var mesh = center.GenerateMeshData(manager);
            Assert.NotNull(mesh);
            var list = type.Translucent ? mesh!.TranslucentVertices : mesh!.Vertices;

            var origin = center.Position * Chunk.Size;
            var boxes = PaneShape.GetBoxesAt(manager, (int)origin.X + 8, (int)origin.Y + y, (int)origin.Z + 8);
            Assert.Equal(1 + 2 * type.Pane!.Rails.Length, boxes.Length);

            var cell = new Vector3(8, y, 8);
            var own = Quads(list)
                .Where(q => q.All(v => v.Layer == type.Side || v.Layer == type.Top || v.Layer == type.Bottom))
                .Where(q => q.All(v => Vector3.Min(v.Position, cell) == cell
                                       && Vector3.Max(v.Position, cell + Vector3.One) == cell + Vector3.One))
                .ToArray();
            Assert.NotEmpty(own);

            // Every corner lies on some box, and every box's top is drawn: the rails keep the caps
            // a cell-tall arm would hand to the pane above
            foreach (var v in own.SelectMany(q => q).Select(v => v.Position - cell))
                Assert.True(boxes.Any(b => Vector3.Min(v, b.Min - new Vector3(1e-4f)) == b.Min - new Vector3(1e-4f)
                                           && Vector3.Max(v, b.Max + new Vector3(1e-4f)) == b.Max + new Vector3(1e-4f)),
                    $"{type.Name}: vertex {v} lies outside every box");
            foreach (var box in boxes)
                Assert.Contains(own, q => q.All(v => MathF.Abs(v.Position.Y - cell.Y - box.Max.Y) < 1e-4f)
                                          && q.Average(v => v.Position.X - cell.X) is var cx && cx > box.Min.X && cx < box.Max.X
                                          && q.Average(v => v.Position.Z - cell.Z) is var cz && cz > box.Min.Z && cz < box.Max.Z);
        }
    }
}
