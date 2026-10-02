using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MeshingTests
{
    public MeshingTests() => TestContent.EnsureLoaded();

    [Fact]
    public void SingleCubeEmitsSixFaces()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        Assert.Empty(mesh.TranslucentVertices);
    }

    [Fact]
    public void SharedFaceIsCulledBetweenCubes()
    {
        // High above the terrain so sunlight is uniform: the two cubes' shared face is culled
        // and the greedy pass merges the remaining coplanar pairs (+-Y, +-Z) into six quads
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        center.SetBlockRaw(8, 8, 8, stone, 0);
        center.SetBlockRaw(9, 8, 8, stone, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);

        // The per-quad path keeps the ten faces apart
        var scalar = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(scalar);
        Assert.Equal(40, scalar.Vertices.Count);
        Assert.Equal(60, scalar.Indices.Count);
    }

    [Fact]
    public void MissingFaceNeighbourAbandonsTheBuild()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        manager.Chunks.TryRemove(Vector3.UnitX, out _);

        Assert.Null(center.GenerateMeshData(manager));
    }

    [Fact]
    public void AllAirChunkBuildsEmpty()
    {
        var (manager, center) = MeshTestWorld.WalledArea();

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.TranslucentVertices);
    }

    [Fact]
    public void GlassQuadsGoToTheTranslucentMesh()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("glass").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Equal(24, mesh.TranslucentVertices.Count);
        Assert.Equal(36, mesh.TranslucentIndices.Count);
    }

    [Fact]
    public void TouchingGlassCullsTheSharedFaces()
    {
        // Glass casts no AO, so like the stone pair above it merges to six quads
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var glass = ContentRegistry.RequireBlock("glass").Id;
        center.SetBlockRaw(8, 8, 8, glass, 0);
        center.SetBlockRaw(9, 8, 8, glass, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.TranslucentVertices.Count);
        Assert.Equal(36, mesh.TranslucentIndices.Count);

        var scalar = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(scalar);
        Assert.Equal(40, scalar.TranslucentVertices.Count);
        Assert.Equal(60, scalar.TranslucentIndices.Count);
    }

    [Fact]
    public void GlassNeverHidesAStoneFace()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        center.SetBlockRaw(9, 8, 8, ContentRegistry.RequireBlock("glass").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(20, mesh.TranslucentVertices.Count);
    }

    [Fact]
    public void WaterSurfaceGoesToTheTranslucentMesh()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.NotEmpty(mesh.TranslucentVertices);
    }

    [Fact]
    public void EnclosedChunkBuildsEmpty()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.TranslucentVertices);
    }

    // One air cell in the neighbour plane that touches the centre exposes exactly one face,
    // which also pins the orientation of each boundary plane the fast path inspects.
    [Theory]
    [InlineData(1, 0, 0, 0, 8, 8)]
    [InlineData(-1, 0, 0, 15, 8, 8)]
    [InlineData(0, 1, 0, 8, 0, 8)]
    [InlineData(0, -1, 0, 8, 15, 8)]
    [InlineData(0, 0, 1, 8, 8, 0)]
    [InlineData(0, 0, -1, 8, 8, 15)]
    public void HoleInNeighbourPlaneEmitsOneFace(int ox, int oy, int oz, int hx, int hy, int hz)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        manager.Chunks[new Vector3(ox, oy, oz)].SetBlockRaw(hx, hy, hz, 0, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(6, mesh.Indices.Count);
    }

    [Fact]
    public void AirPocketInsideSolidChunkStillMeshes()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        center.SetBlockRaw(8, 8, 8, 0, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
    }

    [Fact]
    public void ReturnedBuffersAreRentedAgain()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var first = center.GenerateMeshData(manager);
        Assert.NotNull(first);
        Assert.Equal(0, manager.MeshBuffers.PooledVertexLists);

        manager.MeshBuffers.Return(first);
        Assert.Equal(1, manager.MeshBuffers.PooledVertexLists);

        var second = center.GenerateMeshData(manager);
        Assert.NotNull(second);
        Assert.Same(first.Vertices, second.Vertices);
        Assert.Same(first.Indices, second.Indices);
        Assert.Equal(24, second.Vertices.Count);
        Assert.Equal(0, manager.MeshBuffers.PooledVertexLists);
    }

    [Fact]
    public void OversizedBuffersAreNotRetained()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        mesh.Vertices.Capacity = MeshBufferPool.VertexCapacity * 8;

        manager.MeshBuffers.Return(mesh);
        Assert.Equal(0, manager.MeshBuffers.PooledVertexLists);
    }

    [Fact]
    public void ReturnedTranslucentBuffersAreRentedAgain()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);

        var first = center.GenerateMeshData(manager);
        Assert.NotNull(first);
        Assert.NotEmpty(first.TranslucentVertices);
        Assert.Equal(0, manager.MeshBuffers.PooledTranslucentVertexLists);

        manager.MeshBuffers.Return(first);
        Assert.Equal(1, manager.MeshBuffers.PooledTranslucentVertexLists);

        var second = center.GenerateMeshData(manager);
        Assert.NotNull(second);
        Assert.Same(first.TranslucentVertices, second.TranslucentVertices);
        Assert.Same(first.TranslucentIndices, second.TranslucentIndices);
        Assert.NotEmpty(second.TranslucentVertices);
        Assert.Equal(0, manager.MeshBuffers.PooledTranslucentVertexLists);
    }

    [Fact]
    public void EmptyTranslucentBuffersAreNotRetained()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.TranslucentVertices);

        manager.MeshBuffers.Return(mesh);
        Assert.Equal(0, manager.MeshBuffers.PooledTranslucentVertexLists);
    }

    [Fact]
    public void VertexPackingRoundTrips()
    {
        Assert.Equal(32u, Vertex.Size);
        // Tile-local UVs are floats: a merged quad spans whole tiles, a font glyph a sub-texel
        // rectangle, and both have to come back exactly
        var v = new Vertex(new Vector3(1f, 2f, 3f), 10, 20, 30, 255,
            new Vector2(13.5f, 0.25f), 300, 7f / 15f, 3f / 15f, new Vector3(0f, 0f, 1f), 1f);
        Assert.Equal(13.5f, v.UV.X);
        Assert.Equal(0.25f, v.UV.Y);
        Assert.Equal(300, v.Layer);
        Assert.Equal(0, v.WaterSurface);
        // The single-slice overload lands on layer 0 with the UV untouched
        var sprite = new Vertex(Vector3.Zero, 255, 255, 255, 255, new Vector2(0.125f, 0.875f));
        Assert.Equal(0, sprite.Layer);
        Assert.Equal(0.125f, sprite.UV.X);
        Assert.Equal(7f / 15f, v.Sun / 255f, 3);
        Assert.Equal(3f / 15f, v.Block / 255f, 3);
        Assert.Equal(1, v.Normal & 7);
        Assert.True(v.IsSwaying);
        foreach (var (normal, index) in new[]
                 {
                     (new Vector3(0, 0, -1), 0), (new Vector3(0, 0, 1), 1),
                     (new Vector3(-1, 0, 0), 2), (new Vector3(1, 0, 0), 3),
                     (new Vector3(0, 1, 0), 4), (new Vector3(0, -1, 0), 5),
                 })
            Assert.Equal(index, new Vertex(Vector3.Zero, 255, 255, 255, 255,
                Vector2.Zero, 1f, 0f, normal, 0f).Normal);

        // Bit 3 rides alongside the face index rather than replacing it
        var submerged = new Vertex(Vector3.Zero, 255, 255, 255, 255,
            Vector2.Zero, 1f, 0f, Vector3.UnitY, 0f, submerged: true);
        Assert.Equal(4 | 8, submerged.Normal);
        Assert.Equal(4, submerged.Normal & 7);

        // The 10-bit block-light tint code shares the attribute-5 ushort with the face and flag
        // bits: every code round-trips without disturbing them, and vice versa
        for (var tint = 0; tint < 1024; tint++)
        {
            var t = new Vertex(Vector3.Zero, 255, 255, 255, 255, Vector2.Zero, 0, 1f, 0f,
                -Vector3.UnitX, 1f, submerged: true, tint: (ushort)tint);
            Assert.Equal(tint, t.BlockTint);
            Assert.Equal(2, t.Normal & 7);
            Assert.True(t.IsSubmerged);
            Assert.True(t.IsSwaying);
        }

        // The baked water surface survives its quarter-block quantisation
        foreach (var y in new[] { -44f, -0.25f, 0f, 8f / 9f, 12.75f, 200.5f })
        {
            var encoded = Vertex.EncodeWaterSurface(y);
            Assert.NotEqual(0, encoded);
            Assert.True(MathF.Abs(Vertex.DecodeWaterSurface(encoded) - y) <= 1f / 8f, $"{y}");
        }

        // Every surface code names a higher surface than the one before it
        for (var code = 2; code <= 127; code++)
            Assert.True(Chunk.DecodeSurfaceCode(-32, code) > Chunk.DecodeSurfaceCode(-32, code - 1), $"code {code}");
    }

    /// <summary>Decoded water surface of every submerged top face at world Y
    /// <paramref name="faceY"/>; asserts every vertex that is neither submerged nor a fluid quad
    /// carries no surface.</summary>
    private static List<float> SubmergedTopSurfaces(ChunkMeshData mesh, float faceY)
    {
        var surfaces = new List<float>();
        foreach (var vertex in mesh.Vertices)
        {
            var submerged = (vertex.Normal & 8) != 0;
            if (!submerged)
            {
                // A fluid quad (alpha 254) carries its flow code in the same field instead
                if ((vertex.PackedColor >> 24) != 254) Assert.Equal(0, vertex.WaterSurface);
                continue;
            }
            Assert.NotEqual(0, vertex.WaterSurface);
            if ((vertex.Normal & 7) == 4 && vertex.Position.Y == faceY)
                surfaces.Add(Vertex.DecodeWaterSurface(vertex.WaterSurface));
        }
        Assert.NotEmpty(surfaces);
        return surfaces;
    }

    // ---------------------------------------------------------------- shoreline faces

    /// <summary>A stone column at (8, 8..9, 8) beside one water source at (9, 8, 8) resting on
    /// stone: the column's +X face at y = 8 looks into the water, the one at y = 9 looks into
    /// the air over it, and the top of a lone stone at (8, 8, 8) would sit level with it.</summary>
    private static (ChunkManager Manager, Chunk Center) Shore(bool wall)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        center.SetBlockRaw(8, 8, 8, stone, 0);
        if (wall) center.SetBlockRaw(8, 9, 8, stone, 0);
        center.SetBlockRaw(9, 7, 8, stone, 0);
        center.SetBlockRaw(9, 8, 8, water, FluidState.Source);
        return (manager, center);
    }

    /// <summary>The opaque vertices of the face of <paramref name="face"/> whose lowest corner
    /// sits at <paramref name="bottomY"/> and which touches x = <paramref name="x"/>.</summary>
    private static List<Vertex> FaceVertices(ChunkMeshData mesh, int face, float x, float bottomY)
    {
        var found = new List<Vertex>();
        for (var q = 0; q + 3 < mesh.Vertices.Count; q += 4)
        {
            var quad = mesh.Vertices.GetRange(q, 4);
            if ((quad[0].Normal & 7) != face) continue;
            var minY = MathF.Min(MathF.Min(quad[0].Position.Y, quad[1].Position.Y), MathF.Min(quad[2].Position.Y, quad[3].Position.Y));
            if (minY != bottomY || !quad.Exists(v => v.Position.X == x)) continue;
            found.AddRange(quad);
        }
        Assert.NotEmpty(found);
        return found;
    }

    [Fact]
    public void DryFacesBesideWaterAreNotSubmergedAndCarryNoSurface()
    {
        // The bank wall just over the waterline (+X of the upper stone) and a beach top level
        // with the water beside it look into air: neither may take the water's shading
        var (wallManager, wallCenter) = Shore(wall: true);
        var wallMesh = wallCenter.GenerateMeshData(wallManager);
        Assert.NotNull(wallMesh);
        var (beachManager, beachCenter) = Shore(wall: false);
        var beachMesh = beachCenter.GenerateMeshData(beachManager);
        Assert.NotNull(beachMesh);

        foreach (var v in FaceVertices(wallMesh, 3, 9f, 9f).Concat(FaceVertices(beachMesh, 4, 9f, 9f)))
        {
            Assert.False(v.IsSubmerged);
            Assert.Equal(0, v.WaterSurface);
        }
    }

    [Fact]
    public void FaceLookingIntoWaterIsSubmerged()
    {
        var (manager, center) = Shore(wall: true);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);

        foreach (var v in FaceVertices(mesh, 3, 9f, 8f))
        {
            Assert.True(v.IsSubmerged);
        }
    }

    [Fact]
    public void DryFaceIsNotSubmerged()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.All(mesh.Vertices, v =>
        {
            Assert.False(v.IsSubmerged);
            Assert.Equal(0, v.WaterSurface);
        });
    }

    private static float StackedSurface(int stackHeight, byte topState)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var water = ContentRegistry.RequireBlock("water").Id;
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        for (var y = 9; y < 9 + stackHeight; y++)
            center.SetBlockRaw(8, y, 8, water, y == 8 + stackHeight ? topState : FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        var surfaces = SubmergedTopSurfaces(mesh, 9f);
        Assert.All(surfaces, s => Assert.Equal(surfaces[0], s));
        return surfaces[0];
    }

    [Fact]
    public void SubmergedFaceCarriesItsOwnWaterSurface()
    {
        foreach (var state in new[] { FluidState.Source, FluidState.Flowing(3) })
        {
            // Three cells of water on the stone at y = 8: the top one is y = 11 in this chunk
            var surface = StackedSurface(3, state);
            Assert.True(MathF.Abs(surface - (11f + FluidState.SurfaceHeight(state))) <= 1f / 8f, $"{surface}");
        }
    }

    [Fact]
    public void DeeperColumnRaisesTheBakedSurface()
    {
        var shallow = StackedSurface(2, FluidState.Source);
        var deep = StackedSurface(4, FluidState.Source);
        Assert.True(MathF.Abs(deep - shallow - 2f) <= 1f / 8f, $"{shallow} -> {deep}");
    }

    /// <summary>A stone floor at local (8, 1, 8) of a chunk at <paramref name="position"/>
    /// whose column is water all the way up and out of the padded snapshot's top row.</summary>
    private static float SurfaceOfAColumnLeavingTheSnapshot(Vector3 position)
    {
        var (manager, center) = MeshTestWorld.WalledArea(position);
        var water = ContentRegistry.RequireBlock("water").Id;
        center.SetBlockRaw(8, 1, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        for (var y = 2; y < Chunk.Size; y++) center.SetBlockRaw(8, y, 8, water, FluidState.Source);
        manager.Chunks[position + Vector3.UnitY].SetBlockRaw(8, 0, 8, water, FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        // Vertex positions are chunk-local; the baked surface is world Y
        var surfaces = SubmergedTopSurfaces(mesh, 2f);
        Assert.All(surfaces, s => Assert.Equal(surfaces[0], s));
        return surfaces[0];
    }

    private static int WaterLevelOfColumn(int chunkX, int chunkZ) =>
        Chunk.GetColumnData(chunkX, chunkZ).WaterLevel[(8 << Chunk.SizeShift) | 8];

    [Fact]
    public void ColumnLeavingTheSnapshotUsesTheGeneratedLevel()
    {
        // Any column near the origin with generated water, meshed a few chunks below its level
        var (cx, cz, level) = FindWetColumn();
        var chunkY = (int)MathF.Floor(level / (float)Chunk.Size) - 3;
        var baseY = chunkY * Chunk.Size;
        var snapshotTop = Chunk.DecodeSurfaceCode(baseY, 69);
        Assert.True(level + FluidState.SourceHeight > snapshotTop + 1f);

        var surface = SurfaceOfAColumnLeavingTheSnapshot(new Vector3(cx, chunkY, cz));
        var expected = MathF.Min(level + FluidState.SourceHeight, Chunk.DecodeSurfaceCode(baseY, 127));
        Assert.True(MathF.Abs(surface - expected) <= 0.5f, $"{surface} vs {expected}");
        Assert.True(surface > snapshotTop);
    }

    [Fact]
    public void ColumnLeavingTheSnapshotOverDryGroundCapsAtTheSnapshotTop()
    {
        // High above the terrain the generated level (if any) sits far below the snapshot
        var position = MeshTestWorld.HighAbove;
        var baseY = (int)position.Y * Chunk.Size;
        var level = WaterLevelOfColumn((int)position.X, (int)position.Z);
        Assert.True(level == TerrainGenerator.NoWater || level + FluidState.SourceHeight < Chunk.DecodeSurfaceCode(baseY, 69));

        var surface = SurfaceOfAColumnLeavingTheSnapshot(position);
        Assert.Equal(Chunk.DecodeSurfaceCode(baseY, 69), surface);
    }

    private static (int ChunkX, int ChunkZ, int Level) FindWetColumn()
    {
        for (var r = 0; r < 256; r += 4)
        foreach (var (cx, cz) in new[] { (r, 0), (-r, 0), (0, r), (0, -r), (r, r), (-r, -r) })
        {
            var level = WaterLevelOfColumn(cx, cz);
            if (level != TerrainGenerator.NoWater) return (cx, cz, level);
        }
        throw new InvalidOperationException("no generated water found near the origin");
    }

    [Fact]
    public void SubmergedFacesAreFlagged()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        center.SetBlockRaw(8, 9, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);

        var topVertices = 0;
        foreach (var vertex in mesh.Vertices)
        {
            var face = vertex.Normal & 7;
            var submerged = (vertex.Normal & 8) != 0;
            if (face == 4)
            {
                // The stone's top face is the only one looking into the water above it
                Assert.Equal(9f, vertex.Position.Y);
                Assert.True(submerged);
                topVertices++;
            }
            else
            {
                Assert.False(submerged);
            }
        }

        Assert.Equal(4, topVertices);
    }

    [Fact]
    public void WaterChunkReportsHasWater()
    {
        var (waterManager, waterChunk) = MeshTestWorld.WalledArea();
        waterChunk.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);
        var waterMesh = waterChunk.GenerateMeshData(waterManager);
        Assert.NotNull(waterMesh);
        Assert.True(waterMesh.HasWater);
        Assert.NotEmpty(waterMesh.TranslucentVertices);

        // Glass lands in the same translucent lists, so a translucent mesh is not proof of water
        var (glassManager, glassChunk) = MeshTestWorld.WalledArea();
        glassChunk.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("glass").Id, 0);
        var glassMesh = glassChunk.GenerateMeshData(glassManager);
        Assert.NotNull(glassMesh);
        Assert.False(glassMesh.HasWater);
        Assert.NotEmpty(glassMesh.TranslucentVertices);

        var (stoneManager, stoneChunk) = MeshTestWorld.WalledArea();
        stoneChunk.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var stoneMesh = stoneChunk.GenerateMeshData(stoneManager);
        Assert.NotNull(stoneMesh);
        Assert.False(stoneMesh.HasWater);
    }

    [Fact]
    public void LeavesAndThinWoodPassLightAndCastNoAO()
    {
        var leafIds = TreeSpeciesRegistry.All.Where(s => s.HasLeaves).Select(s => s.LeavesId).ToList();
        Assert.NotEmpty(leafIds);
        foreach (var id in leafIds)
        {
            var leaves = BlockType.ById[id]!;
            Assert.True(leaves.PassesLight);
            Assert.False(leaves.CastsAmbientOcclusion);
            Assert.False(BlockType.Occludes(leaves.Id));
            Assert.False(BlockType.OccludesAO(leaves.Id));
        }

        var woods = BlockType.Types.Values.Where(t => BlockType.IsTreeWood(t.Id)).ToList();
        Assert.NotEmpty(woods);
        foreach (var wood in woods)
        {
            Assert.True(wood.PassesLight);
            Assert.False(wood.CastsAmbientOcclusion);
            Assert.False(BlockType.Occludes(wood.Id));
            Assert.False(BlockType.OccludesAO(wood.Id));
        }

        // Full cubes still darken: the gate must not flatten real occlusion.
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(BlockType.OccludesAO(stone));
    }

    [Fact]
    public void ThinWoodAndLeavesDoNotDarkenNeighbourTopFace()
    {
        // High above the terrain so sunlight is uniform: only AO can vary the shade.
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var leaves = ContentRegistry.RequireBlock("oak_leaves").Id;
        var branch = ContentRegistry.RequireBlock("oak_log").Id;
        var branchState = WoodState.Encode(0, WoodState.ParentDetached, false);
        center.SetBlockRaw(8, 8, 8, stone, 0);

        // Ring the top-face AO neighbourhood with canopy and thin wood: every side/corner
        // sample around the +Y face sits on leaves or a branch. A full-cube neighbour here
        // would drag corners to AO 0 (black); these must leave it at full brightness.
        var ring = new (int x, int z, byte id)[]
        {
            (7, 7, leaves), (7, 8, branch), (7, 9, leaves),
            (8, 7, branch), (8, 9, leaves),
            (9, 7, leaves), (9, 8, branch), (9, 9, leaves),
        };
        foreach (var (x, z, id) in ring)
            center.SetBlockRaw(x, 9, z, id, id == branch ? branchState : (byte)0);

        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);

        var expected = Chunk.ShadeLut[(4 << 2) | 3];
        var topVertices = 0;
        foreach (var vertex in mesh.Vertices)
        {
            if ((vertex.Normal & 7) != 4) continue;
            if (vertex.Position.Y != 9f) continue;
            topVertices++;
            var shade = (byte)(vertex.PackedColor & 0xFF);
            Assert.Equal(expected, shade);
        }

        Assert.Equal(4, topVertices);
    }

    [Fact]
    public void ClearingBorderCellExposesNeighbourFace()
    {
        // High above the terrain so sunlight is uniform: only culling decides the face count.
        var h = MeshTestWorld.HighAbove;
        var manager = new ChunkManager(null!);
        var eastPos = h + Vector3.UnitX;
        foreach (var pos in new[] { eastPos, h, eastPos + Vector3.UnitX, eastPos + Vector3.UnitY,
            eastPos - Vector3.UnitY, eastPos + Vector3.UnitZ, eastPos - Vector3.UnitZ })
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[pos] = chunk;
        }

        var stone = ContentRegistry.RequireBlock("stone").Id;
        manager.Chunks[h].SetBlockRaw(15, 8, 8, stone, 0);
        var east = manager.Chunks[eastPos];
        east.SetBlockRaw(0, 8, 8, stone, 0);

        // The neighbour's west face is culled against the border cell: five faces remain.
        var before = east.GenerateMeshData(manager);
        Assert.NotNull(before);
        Assert.Equal(20, before.Vertices.Count);
        Assert.Equal(30, before.Indices.Count);

        // Clearing the border cell brings the sixth face back with no other change.
        manager.Chunks[h].SetBlockRaw(15, 8, 8, 0, 0);
        var after = east.GenerateMeshData(manager);
        Assert.NotNull(after);
        Assert.Equal(24, after.Vertices.Count);
        Assert.Equal(36, after.Indices.Count);
    }

    /// <summary>
    /// Item C12(b): GenerateBlockData's final recount is vectorised
    /// (BlockCount - _blocks.AsSpan().Count(BlockType.Air)) rather than a scalar foreach over
    /// all 4096 cells. Pins the new form against an independent scalar oracle walking the public
    /// GetLocalBlock, which is the shape of the loop that was deleted -- the fast path that
    /// consumes the count (_solidCount == 0 skips meshing entirely) only ever sees one number,
    /// so a recount that was wrong by any amount but zero would be invisible to every other fact.
    ///
    /// Real generated chunks, not hand-built ones: the deleted loop ran after carving, stamping
    /// and FillWater, so the only interesting inputs are chunks that actually went through all of
    /// it. The column is walked from below the sea floor up past the surface and at least one
    /// chunk must come back non-empty and at least one all-air, so neither the "everything solid"
    /// nor the "above the terrain, early return" branch can carry the fact alone.
    /// </summary>
    [Fact]
    public void SolidCountMatchesAScalarRecount()
    {
        var sawSolid = false;
        var sawAir = false;

        // Bracket the column's own surface instead of assuming it sits in chunk-Y 0..8: with
        // lakes and rivers a column can now be a dry basin whose floor is well below 0, and a
        // fixed range would then see only the air above it.
        var column = Chunk.GetColumnData(3, -5);
        var cyLo = (column.MinHeight >> Chunk.SizeShift) - 1;
        // +TreeGenerator.ReachUp clears every tree canopy above the terrain, so a high chunk
        // is genuinely empty (the early-return branch) rather than full of leaves.
        var cyHi = ((column.MaxHeight + TreeGenerator.ReachUp) >> Chunk.SizeShift) + 1;

        for (var cy = cyLo; cy <= cyHi; cy++)
        {
            var chunk = new Chunk(null!, new Vector3(3, cy, -5), generateBlockData: false);
            chunk.GenerateBlockData();

            var scalar = 0;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                if (chunk.GetLocalBlock(x, y, z) != BlockType.Air)
                    scalar++;

            Assert.Equal(scalar, chunk.SolidCount);
            sawSolid |= scalar > 0;
            sawAir |= scalar == 0;
        }

        Assert.True(sawSolid, "no generated chunk in the column held a block: the fact is vacuous");
        Assert.True(sawAir, "no generated chunk in the column was empty: the early-return branch went untested");
    }
}
