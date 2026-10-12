using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A glass pane is a thin translucent block on glass's path: it never hides a neighbor's face,
/// lets light through and is drawn in the blended pass. Its shape follows its neighbors
/// (<see cref="PaneShape"/>): a post plus an arm toward every neighbouring pane or solid full
/// cube, drawn with no face between two connected panes, and hit and collided with by exactly
/// those arms.
/// </summary>
public sealed class PaneTests
{
    private const int FloorY = 4;

    private readonly byte _pane;
    private readonly byte _stone;

    public PaneTests()
    {
        TestContent.EnsureLoaded();
        _pane = ContentRegistry.RequireBlock("glass_pane").Id;
        _stone = ContentRegistry.RequireBlock("stone").Id;
    }

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    private static IEnumerable<Vertex[]> Quads(List<Vertex> vertices)
    {
        for (var q = 0; q + 3 < vertices.Count; q += 4)
            yield return [vertices[q], vertices[q + 1], vertices[q + 2], vertices[q + 3]];
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // Whether some quad of the list lies flat on the plane where axis `axis` equals `plane`
    private static bool HasFaceOnPlane(List<Vertex> vertices, int axis, float plane) =>
        Quads(vertices).Any(q => q.All(v => MathF.Abs(Axis(v.Position, axis) - plane) < 1e-4f));

    // Whether some quad of the list lies flat on the plane x = planeX inside the cell column (y, z)
    private static bool HasXFace(List<Vertex> vertices, float planeX, int y, int z) =>
        Quads(vertices).Any(q => q.All(v => MathF.Abs(v.Position.X - planeX) < 1e-4f)
                                 && q.Average(v => v.Position.Y) is var cy && cy > y && cy < y + 1
                                 && q.Average(v => v.Position.Z) is var cz && cz > z && cz < z + 1);

    // The arm mask of a pane whose four horizontal neighbors (north, south, west, east) hold ids
    private static int Mask(params byte[] ids) => PaneShape.ConnectionMask(ids, new byte[ids.Length]);

    private static (Vector3 Min, Vector3 Max) Union(IEnumerable<BlockHitbox> boxes) =>
        (boxes.Select(b => b.Min).Aggregate(Vector3.Min), boxes.Select(b => b.Max).Aggregate(Vector3.Max));

    [Fact]
    public void APaneNeverHidesANeighbourAndIsDrawnInTheBlendedPass()
    {
        foreach (var type in BlockType.Types.Values.Where(t => t.Renderer == BlockRenderer.Pane))
        {
            Assert.False(BlockType.Occludes(type.Id), $"{type.Name} occludes its neighbors");
            Assert.False(BlockType.OccludesAO(type.Id), $"{type.Name} darkens its neighbors");
            Assert.True(BlockType.PassesLightById[type.Id], $"{type.Name} blocks light");
        }

        var pane = BlockType.ById[_pane]!;
        Assert.True(pane.Translucent);
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(8, FloorY, 8, _stone, 0);
        center.SetBlockRaw(9, FloorY, 8, _pane, 0);
        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);

        // The stone's face toward the pane still draws, and the pane draws only blended
        Assert.True(HasXFace(mesh!.Vertices, 9f, FloorY, 8), "the stone face behind the pane was culled");
        Assert.NotEmpty(mesh.TranslucentVertices);
        Assert.DoesNotContain(mesh.Vertices, v => v.Layer == pane.Side);
    }

    [Fact]
    public void ArmsReachExactlyTowardPanesAndSolidFullCubes()
    {
        var connects = new[] { _pane, _stone, Block("glass"), Block("dirt") };
        var never = new[]
        {
            BlockType.Air, Block("water"), Block("flower"), Block("torch"), Block("pebble"),
        };

        for (var dir = 0; dir < PaneShape.Directions; dir++)
        {
            var ids = new byte[PaneShape.Directions];
            foreach (var id in connects)
            {
                ids[dir] = id;
                Assert.True(Mask(ids) == 1 << dir, $"no lone arm toward {BlockType.ById[id]!.Name} in direction {dir}");
            }
            foreach (var id in never)
            {
                ids[dir] = id;
                Assert.True(Mask(ids) == 0, $"an arm toward {BlockType.ById[id]?.Name ?? "air"} in direction {dir}");
            }
        }

        // Tree wood connects only where it fills its cell
        var log = Block("oak_log");
        var states = new byte[PaneShape.Directions];
        var logs = new[] { log, log, log, log };
        states[0] = WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false);
        states[1] = WoodState.Detached;
        states[2] = WoodState.Encode(3, WoodState.ParentRoot, false);
        states[3] = WoodState.Encode(3, WoodState.ParentRoot, false);
        Assert.Equal(0b0011, PaneShape.ConnectionMask(logs, states));
    }

    [Fact]
    public void TheRegistryRefusesAPaneThatDeclaresBoxesOrAStateOfItsOwn()
    {
        const string path = "data/blocks/x.json";
        ContentRegistry.ValidatePane(new BlockDefinition(), path);

        var box = System.Text.Json.JsonDocument.Parse("""{ "min": [0, 0, 0], "max": [1, 1, 1] }""").RootElement;
        foreach (var definition in new[]
                 {
                     new BlockDefinition { Hitbox = box },
                     new BlockDefinition { Fluid = true },
                     new BlockDefinition { Falls = true },
                     new BlockDefinition { StackMax = 4 },
                     new BlockDefinition { NeedsSupport = true },
                 })
            Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => ContentRegistry.ValidatePane(definition, path)).Message);
    }

    [Fact]
    public void TheBoxesCoverThePostAndExactlyTheArms()
    {
        static float Volume(Vector3 min, Vector3 max) => (max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z);
        PaneShape.PostBox(out var postMin, out var postMax);
        var postVolume = Volume(postMin, postMax);
        PaneShape.ArmBox(0, out var armMin, out var armMax);
        var armVolume = Volume(armMin, armMax);

        for (var mask = 0; mask < 16; mask++)
        {
            var boxes = PaneShape.Boxes(mask);
            var (min, max) = Union(boxes);

            // Out to the cell edge toward every arm, stopping at the post everywhere else
            Assert.Equal((mask & 4) != 0 ? 0f : postMin.X, min.X);
            Assert.Equal((mask & 8) != 0 ? 1f : postMax.X, max.X);
            Assert.Equal((mask & 1) != 0 ? 0f : postMin.Z, min.Z);
            Assert.Equal((mask & 2) != 0 ? 1f : postMax.Z, max.Z);
            Assert.Equal((0f, 1f), (min.Y, max.Y));

            // The boxes never overlap, so their volume is the post's plus one arm's per arm
            var volume = boxes.Sum(b => b.Size.X * b.Size.Y * b.Size.Z);
            var arms = System.Numerics.BitOperations.PopCount((uint)mask);
            Assert.True(MathF.Abs(volume - (postVolume + arms * armVolume)) < 1e-5f, $"mask {mask}: volume {volume}");
            Assert.True(boxes.Any(b => Vector3.Min(b.Min, postMin) == b.Min && Vector3.Max(b.Max, postMax) == b.Max),
                $"mask {mask}: no box holds the post");
        }
    }

    // A pane at (8, FloorY + 1, 8) on a stone floor with the given cells around it, meshed
    private (ChunkManager Manager, Chunk Center, ChunkMeshData Mesh) MeshPaneAt(params (int X, int Y, int Z, byte Id)[] around)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, FloorY, z, _stone, 0);
        center.SetBlockRaw(8, FloorY + 1, 8, _pane, 0);
        foreach (var (x, y, z, id) in around)
            center.SetBlockRaw(x, y, z, id, 0);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        return (manager, center, mesh!);
    }

    [Fact]
    public void TheDrawnPaneFillsExactlyItsBoxes()
    {
        var y = FloorY + 1;
        var layouts = new[]
        {
            Array.Empty<(int, int, int, byte)>(),
            [(7, y, 8, _pane), (9, y, 8, _pane)],
            [(8, y, 7, _stone), (9, y, 8, _pane)],
            [(8, y, 7, _pane), (8, y, 9, _pane), (7, y, 8, _pane), (9, y, 8, _stone)],
        };

        foreach (var layout in layouts)
        {
            var (manager, center, mesh) = MeshPaneAt(layout);
            var origin = center.Position * Chunk.Size;
            var boxes = PaneShape.GetBoxesAt(manager, (int)origin.X + 8, (int)origin.Y + y, (int)origin.Z + 8);
            var (boxMin, boxMax) = Union(boxes);

            // Only the middle pane's quads: a neighbouring pane's arm never reaches inside its cell
            // and carries no cap on the shared edge, so every one of its quads leaves the cell
            var cell = new Vector3(8, y, 8);
            var own = Quads(mesh.TranslucentVertices)
                .Where(q => q.All(v => Vector3.Min(v.Position, cell) == cell
                                       && Vector3.Max(v.Position, cell + Vector3.One) == cell + Vector3.One))
                .SelectMany(q => q)
                .Select(v => v.Position - cell)
                .ToArray();
            Assert.NotEmpty(own);

            var drawnMin = own.Aggregate(Vector3.Min);
            var drawnMax = own.Aggregate(Vector3.Max);
            Assert.True(Vector3.Distance(drawnMin, boxMin) < 1e-4f && Vector3.Distance(drawnMax, boxMax) < 1e-4f,
                $"drawn {drawnMin}..{drawnMax}, boxes {boxMin}..{boxMax}");
            foreach (var v in own)
                Assert.True(boxes.Any(b => Vector3.Min(v, b.Min - new Vector3(1e-4f)) == b.Min - new Vector3(1e-4f)
                                           && Vector3.Max(v, b.Max + new Vector3(1e-4f)) == b.Max + new Vector3(1e-4f)),
                    $"vertex {v} lies outside every box");
        }
    }

    [Fact]
    public void ConnectedPanesMeetWithNoFaceBetweenThem()
    {
        var y = FloorY + 1;
        // A row along X, and a stack: the shared cell boundaries carry no face
        var (_, _, row) = MeshPaneAt((9, y, 8, _pane));
        Assert.False(HasFaceOnPlane(row.TranslucentVertices, 0, 9f), "two panes in a row draw a face between them");
        // The free ends stop at the posts, not at the cell edges
        Assert.False(HasFaceOnPlane(row.TranslucentVertices, 0, 8f));
        Assert.True(HasFaceOnPlane(row.TranslucentVertices, 0, 8f + PaneShape.Near));
        Assert.True(HasFaceOnPlane(row.TranslucentVertices, 0, 9f + PaneShape.Far));

        // Two stacked panes with the same arms: no face where they meet
        var (_, _, stack) = MeshPaneAt((8, y + 1, 8, _pane), (7, y, 8, _stone), (7, y + 1, 8, _stone));
        Assert.False(HasFaceOnPlane(stack.TranslucentVertices, 1, y + 1f), "two stacked panes draw a face between them");

        // Under a pane without that arm, the lower pane's arm keeps its top
        var (_, _, step) = MeshPaneAt((8, y + 1, 8, _pane), (7, y, 8, _stone));
        Assert.True(HasFaceOnPlane(step.TranslucentVertices, 1, y + 1f), "the top of an arm the pane above lacks was culled");
    }

    [Fact]
    public void LampLightShinesThroughAPaneInAFloor()
    {
        var throughPane = LightUnderTheMiddle(_pane);
        var throughStone = LightUnderTheMiddle(_stone);
        Assert.True(throughPane > throughStone, $"through the pane {throughPane}, through stone {throughStone}");
    }

    // Block light just under the middle of a stone floor spanning the chunk, with a lamp right
    // above the middle cell, which holds either a pane or more stone
    private float LightUnderTheMiddle(byte middle)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var origin = center.Position * Chunk.Size;
        const int floorY = 8;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, floorY, z, x == 8 && z == 8 ? middle : _stone, 0);
        center.SetBlockRaw(8, floorY + 1, 8, Block("lamp"), 0);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        center.PublishLight(mesh!.Light);
        return EntityLight.Sample(manager, origin + new Vector3(8.5f, floorY - 0.5f, 8.5f)).Block;
    }

    [Fact]
    public void TheBodyCollidesWithTheArmsAndNotTheEmptyCell()
    {
        var y = FloorY + 1;
        var (manager, center, _) = MeshPaneAt();
        var origin = center.Position * Chunk.Size;
        var cell = origin + new Vector3(8, y, 8);
        // A body standing over the west half of the pane's cell, beside its post
        var eye = cell + new Vector3(0.1f, Camera.EyeHeight + 0.01f, 0.5f);
        var bodyMin = new Vector3(eye.X - PlayerPhysics.PlayerRadius, eye.Y - Camera.EyeHeight, eye.Z - PlayerPhysics.PlayerRadius);
        var bodyMax = new Vector3(eye.X + PlayerPhysics.PlayerRadius, eye.Y, eye.Z + PlayerPhysics.PlayerRadius);
        int px = (int)cell.X, py = (int)cell.Y, pz = (int)cell.Z;

        // A ray from the north across the west half of the cell, where only a west arm stands
        bool RayHitsTheCell() =>
            BlockRaycaster.Raycast(cell + new Vector3(0.2f, 0.5f, -2.5f), Vector3.UnitZ, 5f, 0f, stopAtRange: false,
                manager, out var hitPosition, out _, out _, out _, includeFluids: false) && hitPosition == cell;

        // A lone pane is its post: the body beside it is clear, and the ray misses it
        Assert.False(TreeWoodShape.OverlapsShape(_pane, manager, px, py, pz, bodyMin, bodyMax));
        Assert.False(PlayerPhysics.BodyCollides(eye, manager));
        Assert.False(RayHitsTheCell());

        // A pane to the west gives it an arm through the body and the ray
        center.SetBlockRaw(7, y, 8, _pane, 0);
        Assert.True(TreeWoodShape.OverlapsShape(_pane, manager, px, py, pz, bodyMin, bodyMax));
        Assert.True(PlayerPhysics.BodyCollides(eye, manager));
        Assert.True(RayHitsTheCell());
    }
}
