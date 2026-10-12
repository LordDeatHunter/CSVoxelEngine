using System;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

public sealed class TreeGraphTests
{
    [Fact]
    public void ChildIsOneFaceStepFromItsParentAndFoundOnlyInThatDirection()
    {
        for (var dir = 0; dir < TreeDirections.Count; dir++)
        {
            var graph = new TreeGraph(3, 10, -7, 1f);
            var child = graph.AddChild(TreeGraph.Root, dir, 1f);

            Assert.True(graph.IsFaceAdjacent(TreeGraph.Root, child));
            Assert.Equal(graph[TreeGraph.Root].X + TreeDirections.Dx[dir], graph[child].X);
            Assert.Equal(graph[TreeGraph.Root].Y + TreeDirections.Dy[dir], graph[child].Y);
            Assert.Equal(graph[TreeGraph.Root].Z + TreeDirections.Dz[dir], graph[child].Z);
            Assert.Equal(child, graph.NodeAt(graph[child].X, graph[child].Y, graph[child].Z));

            for (var other = 0; other < TreeDirections.Count; other++)
                Assert.Equal(other == dir ? child : -1, graph.ChildIn(TreeGraph.Root, other));
        }
    }

    [Fact]
    public void ParentDirDecodesBackOntoTheParentCell()
    {
        var graph = new TreeGraph(0, 5, 0, 1f);
        var node = TreeGraph.Root;
        // A path that uses every direction (the branch off the root adds -X), plus that branch
        foreach (var dir in new[] { TreeDirections.Up, 0, 3, TreeDirections.Up, 1, 3, TreeDirections.Down })
            node = graph.AddChild(node, dir, 1f);
        graph.AddChild(TreeGraph.Root, 2, 1f);

        Assert.Equal(WoodState.ParentRoot, graph[TreeGraph.Root].ParentDir);
        for (var i = 1; i < graph.Count; i++)
        {
            var n = graph[i];
            var state = WoodState.Encode(0, n.ParentDir, false);
            Assert.True(WoodState.TryParentOffset(state, out var dx, out var dy, out var dz));
            var p = graph[n.Parent];
            Assert.Equal((p.X, p.Y, p.Z), (n.X + dx, n.Y + dy, n.Z + dz));
        }
    }

    [Fact]
    public void AddingIntoAnOccupiedCellThrows()
    {
        var graph = new TreeGraph(0, 0, 0, 1f);
        var up = graph.AddChild(TreeGraph.Root, TreeDirections.Up, 1f);
        var side = graph.AddChild(up, 3, 1f);
        var countBefore = graph.Count;

        // Back into the root cell from above, and into a held cell from a different parent
        Assert.Throws<InvalidOperationException>(() => graph.AddChild(up, TreeDirections.Down, 1f));
        Assert.Throws<InvalidOperationException>(() => graph.AddChild(side, 2, 1f));

        // A refused add leaves the graph untouched
        Assert.Equal(countBefore, graph.Count);
        Assert.Equal(1, graph.ChildCount(up));
        Assert.Equal(0, graph.ChildCount(side));
    }

    [Fact]
    public void ChildrenIndexAboveParentsAndCountFollowsAdds()
    {
        var graph = new TreeGraph(0, 0, 0, 1f);
        var rng = new Random(1234);
        var added = 0;
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var parent = rng.Next(graph.Count);
            var dir = rng.Next(TreeDirections.Count);
            var n = graph[parent];
            if (graph.Occupies(n.X + TreeDirections.Dx[dir], n.Y + TreeDirections.Dy[dir], n.Z + TreeDirections.Dz[dir]))
            {
                Assert.Throws<InvalidOperationException>(() => graph.AddChild(parent, dir, 1f));
                continue;
            }
            graph.AddChild(parent, dir, 1f);
            added++;
        }

        Assert.Equal(added + 1, graph.Count);
        var maxY = int.MinValue;
        var childTotal = 0;
        for (var i = 0; i < graph.Count; i++)
        {
            var n = graph[i];
            maxY = Math.Max(maxY, n.Y);
            childTotal += graph.ChildCount(i);
            if (i == TreeGraph.Root)
                Assert.Equal(-1, n.Parent);
            else
                Assert.InRange(n.Parent, 0, i - 1);
        }
        Assert.Equal(graph.Count - 1, childTotal);
        Assert.Equal(maxY, graph.MaxY);
    }

    [Fact]
    public void OppositeIsAnInvolutionAndOffsetsCancel()
    {
        for (var dir = 0; dir < TreeDirections.Count; dir++)
        {
            var opposite = TreeDirections.Opposite(dir);
            Assert.NotEqual(dir, opposite);
            Assert.Equal(dir, TreeDirections.Opposite(opposite));
            Assert.Equal(0, TreeDirections.Dx[dir] + TreeDirections.Dx[opposite]);
            Assert.Equal(0, TreeDirections.Dy[dir] + TreeDirections.Dy[opposite]);
            Assert.Equal(0, TreeDirections.Dz[dir] + TreeDirections.Dz[opposite]);
            Assert.Equal(1, Math.Abs(TreeDirections.Dx[dir]) + Math.Abs(TreeDirections.Dy[dir]) + Math.Abs(TreeDirections.Dz[dir]));
            Assert.Equal(TreeDirections.IsVertical(dir), TreeDirections.Dy[dir] != 0);
            Assert.Equal(!TreeDirections.IsVertical(dir), TreeDirections.IsSide(dir));
        }
    }
}
