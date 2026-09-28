using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The shared fluid-flow core (<see cref="FluidFlow"/>) and the vertex flow code the mesher
/// bakes it into (<see cref="Vertex.EncodeWaterFlow"/>).
/// </summary>
public class FluidFlowTests
{
    [Fact]
    public void WaterFlowCodeRoundTrips()
    {
        const float tolerance = 1f / Vertex.WaterFlowSteps;
        for (var a = 0; a < 64; a++)
        {
            var angle = a * MathF.Tau / 64f;
            foreach (var strength in new[] { 0.1f, 0.35f, 0.6f, 0.9f, 1f })
            foreach (var falling in new[] { false, true })
            {
                var flow = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * strength;
                var (decoded, decodedFalling) = Vertex.DecodeWaterFlow(Vertex.EncodeWaterFlow(flow, falling));
                Assert.InRange(MathF.Abs(decoded.X - flow.X), 0f, tolerance);
                Assert.InRange(MathF.Abs(decoded.Y - flow.Y), 0f, tolerance);
                Assert.Equal(falling, decodedFalling);
            }
        }

        // An over-long flow is clamped to unit length rather than wrapping
        var (clamped, _) = Vertex.DecodeWaterFlow(Vertex.EncodeWaterFlow(new Vector2(-3f, 0f), false));
        Assert.InRange(clamped.X, -1f - tolerance, -1f + tolerance);
    }

    [Fact]
    public void OnlyStillWaterEncodesToZero()
    {
        Assert.Equal(0, Vertex.EncodeWaterFlow(Vector2.Zero, false));
        Assert.NotEqual(0, Vertex.EncodeWaterFlow(Vector2.Zero, true));

        for (var a = 0; a < 48; a++)
        {
            var angle = a * MathF.Tau / 48f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            foreach (var strength in new[] { 1e-4f, 0.004f, 0.02f, 0.3f, 1f })
            {
                var code = Vertex.EncodeWaterFlow(dir * strength, false);
                Assert.NotEqual(0, code);
                var (decoded, _) = Vertex.DecodeWaterFlow(code);
                Assert.NotEqual(Vector2.Zero, decoded);
                // A faint trickle only keeps its dominant axis; anything visible keeps its heading
                if (strength >= 0.3f)
                    Assert.True(Vector2.Dot(Vector2.Normalize(decoded), dir) > 0.98f);
                else
                    Assert.True(Vector2.Dot(Vector2.Normalize(decoded), dir) > 0f);
            }
        }
    }

    [Fact]
    public void SkirtFlowsAwayFromItsSource()
    {
        TestContent.EnsureLoaded();
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(8, 1, 8, water, FluidState.Source);
        chunk.SetBlockRaw(9, 1, 8, water, FluidState.Flowing(1));

        var flow = manager.GetFluidFlowGlobal(9, 1, 8);
        Assert.True(flow.X > 0f);
        Assert.Equal(manager.GetFluidFlowRawGlobal(9, 1, 8).NormalizeSafe(), flow);
    }

    /// <summary>A world of explicit cells; anything unset is air, and y 0 is a stone floor.</summary>
    private readonly struct DictCells(Dictionary<(int, int, int), (byte Id, byte State)> cells, byte floor)
        : IFluidCellReader
    {
        public bool TryGetCell(int x, int y, int z, out byte id, out byte state)
        {
            if (y <= 0)
            {
                id = floor;
                state = 0;
                return true;
            }

            (id, state) = cells.TryGetValue((x, y, z), out var c) ? c : ((byte)0, (byte)0);
            return true;
        }
    }

    [Fact]
    public void EveryCellSharingACornerAgreesOnItsFlow()
    {
        TestContent.EnsureLoaded();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;

        for (var seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var map = new Dictionary<(int, int, int), (byte, byte)>();
            for (var x = 0; x < 4; x++)
            for (var z = 0; z < 4; z++)
            {
                switch (rng.Next(4))
                {
                    case 0: map[(x, 1, z)] = (water, FluidState.Source); break;
                    case 1: map[(x, 1, z)] = (water, FluidState.Flowing(rng.Next(1, 8))); break;
                    case 2: map[(x, 1, z)] = (stone, 0); break;
                    // case 3: air
                }
            }

            var cells = new DictCells(map, stone);
            for (var cx = 0; cx <= 4; cx++)
            for (var cz = 0; cz <= 4; cz++)
            {
                Vector3? shared = null;
                // The up to four cells whose top face has (cx, cz) as a corner
                for (var x = cx - 1; x <= cx; x++)
                for (var z = cz - 1; z <= cz; z++)
                {
                    if (!map.TryGetValue((x, 1, z), out var cell) || cell.Item1 != water)
                        continue;

                    var v = FluidFlow.Corner(cells, x, 1, z, cx == x ? -1 : 1, cz == z ? -1 : 1);
                    if (shared is { } expected)
                        Assert.Equal(expected, v);
                    else
                        shared = v;
                }
            }
        }
    }
    /// <summary>One fluid quad of a built mesh: its four vertices, face index and owning cell.</summary>
    private sealed record FluidQuad(Vertex[] Vertices, int Face, (int X, int Y, int Z) Cell);

    private static List<FluidQuad> FluidQuads(ChunkMeshData mesh)
    {
        var quads = new List<FluidQuad>();
        var verts = mesh.TranslucentVertices;
        for (var q = 0; q + 3 < verts.Count; q += 4)
        {
            var quad = new[] { verts[q], verts[q + 1], verts[q + 2], verts[q + 3] };
            if (quad[0].PackedColor >> 24 != 254) continue; // alpha 254 flags a fluid quad
            var face = quad[0].Normal & 7;
            var centroid = (quad[0].Position + quad[1].Position + quad[2].Position + quad[3].Position) / 4f
                           - 0.01f * Chunk.CubeFaces[face].normal;
            quads.Add(new FluidQuad(quad, face,
                ((int)MathF.Floor(centroid.X), (int)MathF.Floor(centroid.Y), (int)MathF.Floor(centroid.Z))));
        }

        return quads;
    }

    private const int ChannelY = 1;

    /// <summary>A one-wide channel along +X: source, level 1, level 2, then open floor.</summary>
    private static (ChunkManager Manager, ChunkMeshData Mesh) BuildChannel()
    {
        TestContent.EnsureLoaded();
        var (manager, center) = MeshTestWorld.WalledArea();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        {
            for (var z = 0; z < Chunk.Size; z++)
                center.SetBlockRaw(x, 0, z, stone, 0);
            center.SetBlockRaw(x, ChannelY, 7, stone, 0);
            center.SetBlockRaw(x, ChannelY, 9, stone, 0);
        }

        center.SetBlockRaw(8, ChannelY, 8, water, FluidState.Source);
        center.SetBlockRaw(9, ChannelY, 8, water, FluidState.Flowing(1));
        center.SetBlockRaw(10, ChannelY, 8, water, FluidState.Flowing(2));
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        return (manager, mesh!);
    }

    private static Vector3 Flow3(ushort code)
    {
        var (flow, _) = Vertex.DecodeWaterFlow(code);
        return new Vector3(flow.X, 0f, flow.Y);
    }

    [Fact]
    public void MeshedFlowPointsAlongGetFluidFlowGlobal()
    {
        var (manager, mesh) = BuildChannel();
        var reference = manager.GetFluidFlowGlobal(9, ChannelY, 8);
        Assert.NotEqual(Vector3.Zero, reference);

        var quads = FluidQuads(mesh);
        foreach (var v in quads.SelectMany(q => q.Vertices))
        {
            var flow = Flow3(v.WaterSurface);
            if (flow == Vector3.Zero) continue;
            Assert.True(Vector3.Dot(Vector3.Normalize(flow), reference) > 0.98f,
                $"vertex at {v.Position} flows {flow}, the channel flows {reference}");
        }

        foreach (var x in new[] { 9, 10 })
        {
            var top = Assert.Single(quads, q => q.Face == 4 && q.Cell == (x, ChannelY, 8));
            Assert.Contains(top.Vertices, v => Flow3(v.WaterSurface) != Vector3.Zero);
            var mean = top.Vertices.Aggregate(Vector3.Zero, (acc, v) => acc + Flow3(v.WaterSurface)) / 4f;
            Assert.True(Vector3.Dot(mean, manager.GetFluidFlowGlobal(x, ChannelY, 8)) > 0f,
                $"cell {x} bakes mean flow {mean}");
        }
    }

    [Fact]
    public void StillPoolBakesNoFlow()
    {
        TestContent.EnsureLoaded();
        var (manager, center) = MeshTestWorld.WalledArea();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var glass = ContentRegistry.RequireBlock("glass").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);

        // Two layers of sources: glass along -X, open air over stone along +X and both Z sides
        for (var y = 1; y <= 2; y++)
        {
            for (var z = 4; z < 8; z++)
                center.SetBlockRaw(3, y, z, glass, 0);
            for (var x = 4; x < 8; x++)
            for (var z = 4; z < 8; z++)
                center.SetBlockRaw(x, y, z, water, FluidState.Source);
        }

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        var quads = FluidQuads(mesh!);
        Assert.NotEmpty(quads);
        foreach (var v in quads.SelectMany(q => q.Vertices))
            Assert.Equal(0, v.WaterSurface);
    }

    [Fact]
    public void FallingColumnSidesCarryTheFallingFlag()
    {
        TestContent.EnsureLoaded();
        var (manager, center) = MeshTestWorld.WalledArea();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 8, z, stone, 0);
        center.SetBlockRaw(8, 12, 8, water, FluidState.Source);
        for (var y = 9; y <= 11; y++)
            center.SetBlockRaw(8, y, 8, water, FluidState.Falling);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        var sides = FluidQuads(mesh!).Where(q => q.Face < 4 && q.Cell.Y is >= 9 and <= 11).ToList();
        Assert.Equal(12, sides.Count); // four open sides on each of the three falling cells
        foreach (var v in sides.SelectMany(q => q.Vertices))
            Assert.True(Vertex.DecodeWaterFlow(v.WaterSurface).Falling, $"side vertex at {v.Position}");
    }

    [Fact]
    public void SharedFluidVerticesAgreeOnFlow()
    {
        var (_, mesh) = BuildChannel();
        var byPosition = new Dictionary<Vector3, ushort>();
        var shared = 0;
        foreach (var quad in FluidQuads(mesh).Where(q => q.Face == 4))
        foreach (var v in quad.Vertices)
        {
            if (byPosition.TryGetValue(v.Position, out var code))
            {
                shared++;
                Assert.Equal(code, v.WaterSurface);
            }
            else
            {
                byPosition[v.Position] = v.WaterSurface;
            }
        }

        Assert.True(shared > 0, "the channel's tops share no corner");
    }
}
