using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Models;
using VoxelEngine.Data;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// The binary greedy mesher must be invisible: the same pixels as emitting every face on its
/// own, with fewer quads. The differential test is the load-bearing one; the rest pin the
/// eligibility flag, the tiled UVs and the AO rule that decides where merges stop.
/// </summary>
public sealed class GreedyMeshingTests
{
    public GreedyMeshingTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    // ---------------------------------------------------------------- eligibility

    [Fact]
    public void EveryModelRenderedBlockIsGreedyAndNothingElseIs()
    {
        // Every shipped full-cube Model-renderer block is greedy (all derive from the four
        // built-in parents); cross blocks like the flower are Model-rendered but not full
        // cubes, so they keep the per-quad path with the procedural renderers and water
        foreach (var type in BlockType.Types.Values)
        {
            var expected = type.Renderer == BlockRenderer.Model && !type.Fluid
                && type.BakedModel?.IsFullCube == true;
            Assert.True(expected == BlockType.IsGreedy(type.Id), $"{type.Name}: greedy should be {expected}");
        }

        Assert.True(BlockType.IsGreedy(Block("oak_leaves")));
        Assert.True(BlockType.IsGreedy(Block("glass")));
        Assert.False(BlockType.IsGreedy(Block("water")));
        Assert.False(BlockType.IsGreedy(Block("pebble")));
        Assert.False(BlockType.IsGreedy(Block("oak_log")));
        Assert.False(BlockType.IsGreedy(Block("flower")));
        Assert.False(BlockType.IsGreedy(BlockType.Air));
    }

    private static BakedModel BakeCube(Action<string, ModelDefinition.FaceDefinition>? tweak = null,
        bool? shade = null)
    {
        var faces = new Dictionary<string, ModelDefinition.FaceDefinition>();
        foreach (var name in new[] { "north", "south", "west", "east", "up", "down" })
        {
            var face = new ModelDefinition.FaceDefinition { Uv = [0, 0, 16, 16], Texture = "#all", Cullface = name };
            tweak?.Invoke(name, face);
            faces[name] = face;
        }
        var element = new ModelDefinition.ElementDefinition
        {
            From = [0, 0, 0], To = [16, 16, 16], Faces = faces, Shade = shade,
        };
        var model = new BlockModel("test", new Dictionary<string, string> { ["all"] = "stone" }, [element]);
        return ModelBaker.Bake(model);
    }

    [Fact]
    public void OnlyPlainCubeFacesBakeGreedy()
    {
        Assert.True(ContentRegistry.BakesGreedy(BakeCube()));
        Assert.True(ContentRegistry.BakesGreedy(ContentRegistry.RequireBlock("grass").BakedModel!));

        // A cropped or rotated face UV cannot be rebuilt from (face, extent), so the block
        // stays on the per-quad path; so does an unshaded or uncullable face
        Assert.False(ContentRegistry.BakesGreedy(BakeCube((n, f) => { if (n == "up") f.Uv = [0, 0, 8, 8]; })));
        Assert.False(ContentRegistry.BakesGreedy(BakeCube((n, f) => { if (n == "north") f.Rotation = 90; })));
        Assert.False(ContentRegistry.BakesGreedy(BakeCube((n, f) => { if (n == "east") f.Cullface = null; })));
        Assert.False(ContentRegistry.BakesGreedy(BakeCube(shade: false)));
    }

    // ---------------------------------------------------------------- tiled UVs

    private static IEnumerable<Vertex[]> Quads(List<Vertex> vertices, List<ushort> indices)
    {
        for (var q = 0; q < indices.Count; q += 6)
        {
            var o = indices[q];
            yield return [vertices[o], vertices[o + 1], vertices[o + 2], vertices[o + 3]];
        }
    }

    private static Vertex[] SingleQuadOnFace(ChunkMeshData mesh, int face)
    {
        var quads = Quads(mesh.Vertices, mesh.Indices).Where(q => (q[0].Normal & 7) == face).ToList();
        Assert.Single(quads);
        return quads[0];
    }

    [Fact]
    public void SlabTopIsOneQuadWithTiledUvs()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var x = 5; x < 8; x++)
        for (var z = 5; z < 7; z++)
            center.SetBlockRaw(x, 8, z, stone, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        // 3x2x1 slab: six faces, each merged to a single quad
        Assert.Equal(24, mesh.Vertices.Count);

        var top = SingleQuadOnFace(mesh, 4);
        var origin = new Vector3(5, 9, 5);
        var extent = new Vector3(3, 1, 2);
        foreach (var v in top)
            Assert.Equal(Chunk.TiledUv(4, v.Position - origin, extent), v.UV);
        // U runs along X over three tiles, V along Z over two
        Assert.Equal(3f, top.Max(v => v.UV.X) - top.Min(v => v.UV.X));
        Assert.Equal(2f, top.Max(v => v.UV.Y) - top.Min(v => v.UV.Y));

        // The -Z side runs three tiles along X and one up
        var back = SingleQuadOnFace(mesh, 0);
        Assert.Equal(3f, back.Max(v => v.UV.X) - back.Min(v => v.UV.X));
        Assert.Equal(1f, back.Max(v => v.UV.Y) - back.Min(v => v.UV.Y));
    }

    [Fact]
    public void WallSideRunsAlongZAndUp()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var y = 8; y < 10; y++)
        for (var z = 4; z < 7; z++)
            center.SetBlockRaw(5, y, z, stone, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);

        var east = SingleQuadOnFace(mesh, 3);
        var origin = new Vector3(5, 8, 4);
        var extent = new Vector3(1, 2, 3);
        foreach (var v in east)
            Assert.Equal(Chunk.TiledUv(3, v.Position - origin, extent), v.UV);
        Assert.Equal(3f, east.Max(v => v.UV.X) - east.Min(v => v.UV.X));
        Assert.Equal(2f, east.Max(v => v.UV.Y) - east.Min(v => v.UV.Y));
    }

    // ---------------------------------------------------------------- AO decides the merges

    [Fact]
    public void PillarOnAFloorSplitsTheTopFace()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);

        // A bare floor is one quad per face
        var bare = center.GenerateMeshData(manager);
        Assert.NotNull(bare);
        Assert.Equal(24, bare.Vertices.Count);

        center.SetBlockRaw(8, 1, 8, stone, 0);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);

        var floorTop = Quads(mesh.Vertices, mesh.Indices)
            .Where(q => (q[0].Normal & 7) == 4 && q[0].Position.Y == 1f).ToList();
        Assert.True(floorTop.Count > 1, $"{floorTop.Count} top quads");

        // The cells around the pillar carry darker corners and never merged with the open floor
        var lit = Chunk.ShadeLut[(4 << 2) | 3];
        Assert.Contains(floorTop, q => q.Any(v => (v.PackedColor & 0xFF) < lit));
        Assert.Contains(floorTop, q => q.All(v => (v.PackedColor & 0xFF) == lit));
    }

    [Fact]
    public void LampLitFloorMergesOnlyWhereLightIsConstant()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);
        // Hung above the floor's AO neighbourhood (the y = 1 row), so only light varies
        center.SetBlockRaw(8, 3, 8, Block("lamp"), 0);

        AssertGreedyMatchesPerQuad(manager, center);

        var mesh = center.GenerateMeshData(manager, greedy: true);
        Assert.NotNull(mesh);
        var floorTop = Quads(mesh.Vertices, mesh.Indices)
            .Where(q => (q[0].Normal & 7) == 4 && q.All(v => v.Position.Y == 1f)).ToList();

        // AO is uniform over the whole floor, so any split comes from the lamp's light
        var shade = floorTop[0][0].PackedColor & 0xFF;
        Assert.All(floorTop, q => Assert.All(q, v => Assert.Equal(shade, v.PackedColor & 0xFF)));
        Assert.True(floorTop.Count > 1, $"{floorTop.Count} top quads");
        Assert.Contains(floorTop, q => q.Any(v => v.Block != q[0].Block));
    }

    [Fact]
    public void StripBesideAWallMergesAlongTheWall()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);
        // A wall along X at z = 0, continued into the X neighbors so every strip cell sees
        // the same corners
        for (var x = 0; x < Chunk.Size; x++) center.SetBlockRaw(x, 1, 0, stone, 0);
        manager.Chunks[MeshTestWorld.HighAbove - Vector3.UnitX].SetBlockRaw(Chunk.Size - 1, 1, 0, stone, 0);
        manager.Chunks[MeshTestWorld.HighAbove + Vector3.UnitX].SetBlockRaw(0, 1, 0, stone, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);

        // +Y quads: the wall top, the shaded strip beside the wall (16 x 1) and the open floor
        // (16 x 14). The strip's AO differs between its two Z edges, so it never merges across
        // Z, but is constant along X, so it merges along the wall.
        var tops = Quads(mesh.Vertices, mesh.Indices).Where(q => (q[0].Normal & 7) == 4).ToList();
        Assert.Equal(3, tops.Count);

        var strip = tops.Single(q => q.All(v => v.Position.Y == 1f && v.Position.Z is 1f or 2f));
        Assert.Equal(0f, strip.Min(v => v.Position.X));
        Assert.Equal(16f, strip.Max(v => v.Position.X));
        var lit = Chunk.ShadeLut[(4 << 2) | 3];
        Assert.All(strip.Where(v => v.Position.Z == 1f), v => Assert.True((v.PackedColor & 0xFF) < lit));
        Assert.All(strip.Where(v => v.Position.Z == 2f), v => Assert.Equal(lit, (byte)(v.PackedColor & 0xFF)));

        var open = tops.Single(q => q.All(v => v.Position.Y == 1f && v.Position.Z is 2f or 16f));
        Assert.Equal(0f, open.Min(v => v.Position.X));
        Assert.Equal(16f, open.Max(v => v.Position.X));
    }

    // ---------------------------------------------------------------- ushort guard

    [Fact]
    public void FullLeafChunkStaysWithinUshortIndices()
    {
        // Leaves never occlude each other, so every one of the 24576 faces is visible: the
        // per-quad path would overrun a ushort index space and must stop instead
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        MeshTestWorld.FillAll(center, Block("oak_leaves"));

        var scalar = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(scalar);
        Assert.True(scalar.Vertices.Count <= ushort.MaxValue + 1);
        Assert.Equal(scalar.Vertices.Count / 4 * 6, scalar.Indices.Count);
        Assert.All(scalar.Indices, i => Assert.True(i < scalar.Vertices.Count));

        // Merged, the canopy's interior collapses to a few planes per slice
        var greedy = center.GenerateMeshData(manager);
        Assert.NotNull(greedy);
        Assert.True(greedy.Vertices.Count < scalar.Vertices.Count / 8, $"{greedy.Vertices.Count} vertices");
        Assert.All(greedy.Indices, i => Assert.True(i < greedy.Vertices.Count));
    }

    // ---------------------------------------------------------------- differential equivalence

    /// <summary>What one unit cell face looks like once rendered, independent of how many
    /// quads it was drawn with: attributes plus the shade, sun and lamp light, decoded lamp tint
    /// color and UV interpolated at its four corners. <c>Flags</c> is the packed normal above the face index
    /// (submerged bit 3, biome tint bit 4, sway bit 5, ...), so comparing it compares IsBiomeTinted too.</summary>
    private sealed record CellFace(int Face, int Flags, int Alpha, int Layer, bool Sway, int WaterSurface, int List,
        Vector3[] Shade, float[] Sun, float[] Block, Vector3[] Tint, float[] U, float[] V);

    private static float Bilinear(float[,] c, float s, float t) =>
        c[0, 0] * (1 - s) * (1 - t) + c[1, 0] * s * (1 - t) + c[0, 1] * (1 - s) * t + c[1, 1] * s * t;

    private static Vector3 Bilinear(Vector3[,] c, float s, float t) =>
        c[0, 0] * ((1 - s) * (1 - t)) + c[1, 0] * (s * (1 - t)) + c[0, 1] * ((1 - s) * t) + c[1, 1] * (s * t);

    private static Vector3 DecodeTint(Vertex v) => LightTint.Decode(v.BlockTint, BlockLightPalette.Linear);

    private static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static bool IsInteger(float f) => MathF.Abs(f - MathF.Round(f)) < 1e-4f;

    /// <summary>
    /// Splits a mesh into unit cell faces. Every axis-aligned quad with integer corners is cut
    /// into the 1x1 cells it covers (keyed by the voxel it belongs to and its face), with shade,
    /// light and UV evaluated by bilinear interpolation at the cell's corners. Anything else (sub-cell
    /// boxes, sloped fluid tops) is kept verbatim in <paramref name="raw"/>: the two paths emit
    /// those with the same code, so they must match byte for byte.
    /// </summary>
    private static void Rasterise(List<Vertex> vertices, List<ushort> indices, int list,
        Dictionary<(int X, int Y, int Z, int Face, int Layer), CellFace> cells, List<string> raw)
    {
        Assert.Equal(0, indices.Count % 6);
        Assert.Equal(vertices.Count, indices.Count / 6 * 4);

        for (var q = 0; q < indices.Count; q += 6)
        {
            var o = indices[q];
            Assert.Equal(new[] { o, o + 1, o + 2, o, o + 2, o + 3 }, indices.Skip(q).Take(6).Select(i => (int)i));
            var quad = new[] { vertices[o], vertices[o + 1], vertices[o + 2], vertices[o + 3] };
            var v0 = quad[0];
            foreach (var v in quad)
            {
                // Bits 0-5 (face, submerged, biome tint, sway) are per quad; the tint code above them is
                // per corner and is compared through the rasterised corners instead
                Assert.Equal(v0.Normal & 0x3F, v.Normal & 0x3F);
                Assert.Equal(v0.Layer, v.Layer);
                Assert.Equal(v0.IsSwaying, v.IsSwaying);
                // A fluid quad (alpha 254) carries its flow code per corner by design, so only the
                // falling bit (bit 14) has to be per quad; everything else keeps one value per quad
                if (v0.PackedColor >> 24 == 254)
                    Assert.Equal(v0.WaterSurface >> 14, v.WaterSurface >> 14);
                else
                    Assert.Equal(v0.WaterSurface, v.WaterSurface);
                Assert.Equal(v0.PackedColor >> 24, v.PackedColor >> 24);
            }

            var face = v0.Normal & 7;
            var plane = face switch { 0 or 1 => 2, 2 or 3 => 0, _ => 1 };
            var min = quad.Aggregate(quad[0].Position, (m, v) => Vector3.Min(m, v.Position));
            var max = quad.Aggregate(quad[0].Position, (m, v) => Vector3.Max(m, v.Position));
            var extent = max - min;

            var a = plane == 0 ? 1 : 0;
            var b = plane == 2 ? 1 : 2;
            // Grid-aligned: flat on its plane, whole cells wide, and every corner on the
            // rectangle (a sloped fluid side has an integer bounding box but a mid-height corner)
            var gridAligned = Axis(extent, plane) == 0f
                              && Axis(extent, a) >= 1f && Axis(extent, b) >= 1f
                              && IsInteger(min.X) && IsInteger(min.Y) && IsInteger(min.Z)
                              && IsInteger(max.X) && IsInteger(max.Y) && IsInteger(max.Z)
                              && quad.All(v => (Axis(v.Position, a) == Axis(min, a) || Axis(v.Position, a) == Axis(max, a))
                                               && (Axis(v.Position, b) == Axis(min, b) || Axis(v.Position, b) == Axis(max, b)));
            if (!gridAligned)
            {
                raw.Add(string.Join(" | ", quad.Select(v =>
                    $"{v.Position} {v.PackedColor:X8} {v.Uv} {v.Sun} {v.Block} {v.Normal & 0x3F} {v.IsSwaying} {v.BlockTint} {v.Layer} {v.WaterSurface}")) + $" list {list}");
                continue;
            }

            var shade = new Vector3[2, 2];
            var sun = new float[2, 2];
            var block = new float[2, 2];
            var tint = new Vector3[2, 2];
            var us = new float[2, 2];
            var vs = new float[2, 2];
            var seen = new bool[2, 2];
            foreach (var v in quad)
            {
                var s = (Axis(v.Position, a) - Axis(min, a)) / Axis(extent, a);
                var t = (Axis(v.Position, b) - Axis(min, b)) / Axis(extent, b);
                Assert.True(s is 0f or 1f && t is 0f or 1f, $"corner not on the quad's rectangle: {v.Position}");
                seen[(int)s, (int)t] = true;
                shade[(int)s, (int)t] = new Vector3(v.PackedColor & 0xFF, (v.PackedColor >> 8) & 0xFF, (v.PackedColor >> 16) & 0xFF);
                sun[(int)s, (int)t] = v.Sun;
                block[(int)s, (int)t] = v.Block;
                tint[(int)s, (int)t] = DecodeTint(v);
                us[(int)s, (int)t] = v.Uv.X;
                vs[(int)s, (int)t] = v.Uv.Y;
            }
            Assert.True(seen[0, 0] && seen[1, 0] && seen[0, 1] && seen[1, 1], "quad does not span its rectangle");

            var ea = (int)Axis(extent, a);
            var eb = (int)Axis(extent, b);
            var positive = face is 1 or 3 or 4;
            for (var i = 0; i < ea; i++)
            for (var j = 0; j < eb; j++)
            {
                var cell = new int[3];
                cell[plane] = (int)MathF.Round(Axis(min, plane)) - (positive ? 1 : 0);
                cell[a] = (int)MathF.Round(Axis(min, a)) + i;
                cell[b] = (int)MathF.Round(Axis(min, b)) + j;

                var cornerShade = new Vector3[4];
                var cornerSun = new float[4];
                var cornerBlock = new float[4];
                var cornerTint = new Vector3[4];
                var cornerU = new float[4];
                var cornerV = new float[4];
                for (var c = 0; c < 4; c++)
                {
                    var s = (i + (c & 1)) / (float)ea;
                    var t = (j + (c >> 1)) / (float)eb;
                    cornerShade[c] = Bilinear(shade, s, t);
                    cornerSun[c] = Bilinear(sun, s, t);
                    cornerBlock[c] = Bilinear(block, s, t);
                    cornerTint[c] = Bilinear(tint, s, t);
                    cornerU[c] = Bilinear(us, s, t);
                    cornerV[c] = Bilinear(vs, s, t);
                }

                // Keyed by layer too: a biome-tint overlay is a second, coplanar quad over its base (grass
                // side: dirt, then the tinted band), not the same cell face drawn twice
                var key = (cell[0], cell[1], cell[2], face, (int)v0.Layer);
                Assert.False(cells.ContainsKey(key), $"cell face {key} drawn twice");
                cells[key] = new CellFace(face, (v0.Normal & 0x3F) >> 3, (int)(v0.PackedColor >> 24), v0.Layer, v0.IsSwaying, v0.WaterSurface, list,
                    cornerShade, cornerSun, cornerBlock, cornerTint, cornerU, cornerV);
            }
        }
    }

    private static (Dictionary<(int, int, int, int, int), CellFace> Cells, List<string> Raw) Rasterise(ChunkMeshData mesh)
    {
        var cells = new Dictionary<(int, int, int, int, int), CellFace>();
        var raw = new List<string>();
        Rasterise(mesh.Vertices, mesh.Indices, 0, cells, raw);
        Rasterise(mesh.TranslucentVertices, mesh.TranslucentIndices, 1, cells, raw);
        raw.Sort(StringComparer.Ordinal);
        return (cells, raw);
    }

    private static void FillRandom(Chunk chunk, Random rng, byte[] palette, byte water)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var id = palette[rng.Next(palette.Length)];
            // Tree wood gets a random graph state, so thin, full and detached cells and every
            // parent direction all turn up next to each other
            var state = id == water ? FluidState.Source
                : BlockType.IsTreeWood(id) ? (byte)rng.Next(256)
                : (byte)0;
            chunk.SetBlockRaw(x, y, z, id, state);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 8)]
    public void GreedyMeshDrawsExactlyWhatThePerQuadPathDraws(int seed, int chunkY)
    {
        // Random soup of every kind of block, in the centre and in the six neighbors (so halo
        // culling and cross-border AO are exercised), at chunk heights where sunlight varies
        var centre = new Vector3(0, chunkY, 0);
        var (manager, center) = MeshTestWorld.WalledArea(centre);
        var water = Block("water");
        byte[] palette =
        [
            0, 0, 0, 0, 0, 0, 0, 0,
            Block("stone"), Block("stone"), Block("dirt"), Block("grass"), Block("oak_log"),
            Block("glass"), Block("glass"), Block("oak_leaves"), Block("oak_leaves"), Block("lamp"), Block("blue_lamp"),
            Block("pebble"), Block("branch_pile"), Block("spruce_log"), water,
        ];
        var rng = new Random(seed);
        // Fill in a fixed order: the dictionary's enumeration order changes per process
        foreach (var chunk in manager.Chunks.Values.OrderBy(c => c.Position.X).ThenBy(c => c.Position.Y).ThenBy(c => c.Position.Z))
            FillRandom(chunk, rng, palette, water);

        AssertGreedyMatchesPerQuad(manager, center);
    }

    /// <summary>Meshes <paramref name="center"/> both ways and requires the two outputs to draw
    /// the same unit cell faces with the same attributes. Returns the per-quad cells.</summary>
    private static Dictionary<(int, int, int, int, int), CellFace> AssertGreedyMatchesPerQuad(ChunkManager manager, Chunk center)
    {
        var greedy = center.GenerateMeshData(manager, greedy: true);
        var scalar = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(greedy);
        Assert.NotNull(scalar);
        // Neither list may have hit the ushort guard, or the two outputs legitimately differ
        Assert.True(scalar.Vertices.Count < 60000, $"{scalar.Vertices.Count} opaque vertices");
        Assert.True(scalar.TranslucentVertices.Count < 60000, $"{scalar.TranslucentVertices.Count} translucent vertices");
        Assert.Equal(scalar.HasWater, greedy.HasWater);

        var (gCells, gRaw) = Rasterise(greedy);
        var (sCells, sRaw) = Rasterise(scalar);

        Assert.Equal(sRaw, gRaw);
        Assert.Equal(sCells.Count, gCells.Count);
        foreach (var (key, expected) in sCells)
        {
            Assert.True(gCells.TryGetValue(key, out var actual), $"greedy mesh lacks cell face {key}");
            Assert.Equal(expected.Face, actual!.Face);
            Assert.Equal(expected.Alpha, actual.Alpha);
            Assert.Equal(expected.Layer, actual.Layer);
            Assert.Equal(expected.Sway, actual.Sway);
            Assert.Equal(expected.Flags, actual.Flags);
            Assert.Equal(expected.WaterSurface, actual.WaterSurface);
            Assert.Equal(expected.List, actual.List);
            for (var c = 0; c < 4; c++)
            {
                // All three channels: a biome-tinted face carries shade * tint per channel, so a
                // tint bug in one channel must not hide behind the others
                var ds = Vector3.Abs(expected.Shade[c] - actual.Shade[c]);
                Assert.True(ds.X <= 1.01f && ds.Y <= 1.01f && ds.Z <= 1.01f,
                    $"cell face {key} corner {c}: rgb {expected.Shade[c]} vs {actual.Shade[c]}");
                Assert.True(MathF.Abs(expected.Sun[c] - actual.Sun[c]) <= 1.01f,
                    $"cell face {key} corner {c}: sun {expected.Sun[c]} vs {actual.Sun[c]}");
                Assert.True(MathF.Abs(expected.Block[c] - actual.Block[c]) <= 1.01f,
                    $"cell face {key} corner {c}: block light {expected.Block[c]} vs {actual.Block[c]}");
                var dt = Vector3.Abs(expected.Tint[c] - actual.Tint[c]);
                Assert.True(dt.X <= 1e-3f && dt.Y <= 1e-3f && dt.Z <= 1e-3f,
                    $"cell face {key} corner {c}: tint {expected.Tint[c]} vs {actual.Tint[c]}");
                // A merged quad's UV runs past 1, so the two agree modulo whole tiles
                var du = actual.U[c] - expected.U[c];
                var dv = actual.V[c] - expected.V[c];
                Assert.True(IsInteger(du) && IsInteger(dv),
                    $"cell face {key} corner {c}: uv ({expected.U[c]}, {expected.V[c]}) vs ({actual.U[c]}, {actual.V[c]})");
            }
        }

        // Same picture from no more quads. Random soup merges almost nothing (a few quads in ten
        // thousand, sometimes none at all), so this is a bound, not a count; the slab, floor and
        // wall tests above pin the merging itself.
        Assert.True(greedy.Vertices.Count <= scalar.Vertices.Count,
            $"opaque vertices greedy {greedy.Vertices.Count} vs per-quad {scalar.Vertices.Count}");
        Assert.True(greedy.TranslucentVertices.Count <= scalar.TranslucentVertices.Count,
            $"translucent vertices greedy {greedy.TranslucentVertices.Count} vs per-quad {scalar.TranslucentVertices.Count}");
        return sCells;
    }

    [Fact]
    public void TwoLampColoursMeshTheSameBothWays()
    {
        // A walled stone floor lit by a default lamp and a coloured one six cells apart, hung two
        // cells above it, so the floor between them blends two palette slots
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);
        center.SetBlockRaw(5, 3, 8, Block("lamp"), 0);
        center.SetBlockRaw(11, 3, 8, Block("blue_lamp"), 0);

        AssertGreedyMatchesPerQuad(manager, center);

        // Anti-vacuity: the floor really carries a two-slot blend, so the parity compared colors
        var mesh = center.GenerateMeshData(manager, greedy: true);
        Assert.NotNull(mesh);
        Assert.Contains(mesh.Vertices, v => (v.Normal & 7) == 4 && v.Position.Y == 1f
                                            && ((v.BlockTint >> 3) & 7) != (v.BlockTint & 7)
                                            && (v.BlockTint >> 6) > 0);
    }

    [Fact]
    public void ShorelineMeshesTheSameBothWays()
    {
        // A pond sunk into stone ground level with its surface (dry beach tops all round) and a
        // low wall along one bank (dry bank faces just over the waterline)
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        var water = Block("water");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var pond = x is >= 4 and <= 11 && z is >= 4 and <= 11;
            for (var y = 0; y <= 5; y++)
                center.SetBlockRaw(x, y, z, pond && y >= 3 ? water : stone, pond && y >= 3 ? FluidState.Source : (byte)0);
            if (x == 3) for (var y = 6; y <= 7; y++) center.SetBlockRaw(x, y, z, stone, 0);
        }

        var cells = AssertGreedyMatchesPerQuad(manager, center);

        // The pond bed and the bank wall under the waterline really are submerged, so the
        // parity above compared baked water surfaces as well as dry faces
        Assert.Contains(cells.Values, c => (c.Flags & 1) != 0 && c.Face == 4);
        Assert.Contains(cells.Values, c => (c.Flags & 1) != 0 && c.Face == 3);
    }

    [Fact]
    public void MergesNeverCrossTwoWaterSurfaces()
    {
        // A stone floor under water one deep over the low-x half and three deep over the other:
        // the two halves' faces agree on everything but the surface they sit under
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        var water = Block("water");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            center.SetBlockRaw(x, 4, z, stone, 0);
            var depth = x < Chunk.Size / 2 ? 1 : 3;
            for (var y = 5; y < 5 + depth; y++) center.SetBlockRaw(x, y, z, water, FluidState.Source);
        }

        var cells = AssertGreedyMatchesPerQuad(manager, center);

        var stoneTop = ContentRegistry.RequireBlock("stone").FaceTiles[4];
        var shallow = cells[(2, 4, 8, 4, stoneTop)].WaterSurface;
        var deep = cells[(Chunk.Size - 3, 4, 8, 4, stoneTop)].WaterSurface;
        Assert.NotEqual(0, shallow);
        Assert.True(Vertex.DecodeWaterSurface((ushort)deep) > Vertex.DecodeWaterSurface((ushort)shallow));
    }
}
