using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>
/// Rules of the far-LOD canopy proxy (<see cref="LodCrownProxy"/>, <see cref="LodLevelSpec.ProxyCanopy"/>):
/// the proxy canopy stands only over trees <see cref="TreeGenerator.TryGetTree"/> places (the
/// one placement source the grown canopy shares), every crown profile peaks at the trunk and
/// never climbs with distance, a crown never leaves its species' reach, the crown roll is a pure
/// function of the column, and the rings that read the proxy never run tree growth. In the
/// shape-cache collection because one fact resets the tree caches.
/// </summary>
[Collection("Worldgen shape cache")]
public sealed class LodCanopyProxyTests
{
    public LodCanopyProxyTests() => TestContent.EnsureLoaded();

    private readonly record struct Trunk(int X, int Z, TreeSpecies Species, int BaseY);

    private static IEnumerable<TreeSpecies> ProxySpecies =>
        TreeSpeciesRegistry.All.Where(s => s.HasLeaves && s.Worldgen != null);

    /// <summary>The widest proxy crown of any leafy worldgen species.</summary>
    private static int MaxCrownRadius => ProxySpecies.Max(s => s.CrownRadius);

    /// <summary>A taiga chunk column rooting a leafy tree, searched like the snow facts do.</summary>
    private static (int Cx, int Cz) FindTaigaTreeColumn()
    {
        for (var cx = -48; cx <= 48; cx++)
        for (var cz = -48; cz <= 48; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Taiga)
                continue;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                if (TreeGenerator.TryGetTree(cx * Chunk.Size + x, cz * Chunk.Size + z, out var species, out _)
                    && species.HasLeaves)
                    return (cx, cz);
        }

        throw new InvalidOperationException("no taiga chunk column with a leafy tree within 48 chunks");
    }

    /// <summary>The chunk columns the placement facts check: a forest 3x3 and one taiga column.</summary>
    private static List<(int Cx, int Cz)> SampleChunkColumns()
    {
        var (fx, fz) = TreeWorldgenFixture.FindForestTreeColumn();
        var columns = new List<(int Cx, int Cz)>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            columns.Add((fx + dx, fz + dz));
        columns.Add(FindTaigaTreeColumn());
        return columns;
    }

    /// <summary>Every leafy trunk TryGetTree places within <paramref name="margin"/> of the chunk column.</summary>
    private static List<Trunk> LeafyTrunksAround(int cx, int cz, int margin)
    {
        var trunks = new List<Trunk>();
        var x0 = cx * Chunk.Size;
        var z0 = cz * Chunk.Size;
        for (var x = x0 - margin; x < x0 + Chunk.Size + margin; x++)
        for (var z = z0 - margin; z < z0 + Chunk.Size + margin; z++)
            if (TreeGenerator.TryGetTree(x, z, out var species, out var baseY) && species.HasLeaves)
                trunks.Add(new Trunk(x, z, species, baseY));
        return trunks;
    }

    private static int Chebyshev(int ax, int az, int bx, int bz) => Math.Max(Math.Abs(ax - bx), Math.Abs(az - bz));

    [Fact]
    public void ProxyPlacementEqualsRealPlacement()
    {
        var radius = MaxCrownRadius;
        var reach = TreeGenerator.ReachHorizontal;
        var trunkColumnsWithCrown = 0;

        foreach (var (cx, cz) in SampleChunkColumns())
        {
            // Read the cached columns directly: CanopyTop/TryGetCanopy first apply the edit gate,
            // which parallel LOD-edit tests poison, and that gate is CanopyDisturbedTests' rule
            var proxy = TreeGenerator.GetProxyCanopyColumns(cx, cz);
            var real = TreeGenerator.GetCanopyColumns(cx, cz);
            var trunks = LeafyTrunksAround(cx, cz, reach);

            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var x = cx * Chunk.Size + lx;
                var z = cz * Chunk.Size + lz;
                var i = (lx << Chunk.SizeShift) | lz;
                var proxyTop = proxy.Tops[i];
                var overhanging = trunks.Where(t => Chebyshev(t.X, t.Z, x, z) <= radius).ToList();

                if (overhanging.Count == 0)
                {
                    Assert.True(proxyTop == 0, $"proxy canopy at ({x}, {z}) with no leafy trunk within {radius}");
                }
                else if (proxyTop > 0)
                {
                    // The top is some overhanging trunk's crown, worn in that trunk's leaves
                    Assert.Contains(overhanging, t =>
                        t.Species.LeavesId == proxy.LeafBlocks[i]
                        && proxyTop > t.BaseY && proxyTop <= t.BaseY + t.Species.CrownHeightMax);
                }

                // A trunk whose rolled crown has height stands under at least its own apex
                if (TreeGenerator.TryGetTree(x, z, out var species, out var baseY) && species.HasLeaves)
                {
                    var height = TreeGenerator.CrownHeightOf(species, x, z);
                    if (height >= 1)
                    {
                        trunkColumnsWithCrown++;
                        Assert.True(proxyTop >= baseY + height,
                            $"trunk ({x}, {z}) rolled crown {height} over base {baseY} but the proxy top is {proxyTop}");
                    }
                }

                // The grown canopy comes from the same placement
                if (real.Tops[i] > 0)
                {
                    var near = trunks.Where(t => Chebyshev(t.X, t.Z, x, z) <= reach).ToList();
                    Assert.True(near.Count > 0, $"grown canopy at ({x}, {z}) with no leafy trunk within {reach}");
                    Assert.Contains(near, t => t.Species.LeavesId == real.LeafBlocks[i]);
                }
            }
        }

        Assert.True(trunkColumnsWithCrown > 0, "no sampled trunk rolled a proxy crown");
    }

    [Fact]
    public void ProxyCrownStaysWithinTheSpeciesReach()
    {
        foreach (var species in ProxySpecies)
        {
            var (horizontal, up, _) = TreeRasterizer.ReachOf(species);
            Assert.True(species.CrownRadius <= horizontal,
                $"{species.Name}: lod crown radius {species.CrownRadius} exceeds its reach {horizontal}");
            Assert.True(species.CrownHeightMax <= up,
                $"{species.Name}: lod crown height {species.CrownHeightMax} exceeds its reach {up}");

            var r = species.CrownRadius;
            for (var height = species.CrownHeightMin; height <= species.CrownHeightMax; height++)
            for (var dx = -r; dx <= r; dx++)
            for (var dz = -r; dz <= r; dz++)
                Assert.True(LodCrownProxy.TopOffset(species.LodProfile, height, r, dx, dz) <= up,
                    $"{species.Name}: proxy crown at ({dx}, {dz}) rises above its reach {up}");
        }
    }

    [Fact]
    public void CrownProfilesAreNonIncreasingFromTheTrunk()
    {
        (int Height, int Radius)[] crowns = [(0, 0), (5, 0), (1, 4), (7, 2), (10, 3), (12, 5)];
        foreach (var profile in Enum.GetValues<LodCrownProfile>())
        foreach (var (height, radius) in crowns)
        {
            Assert.Equal(height, LodCrownProxy.TopOffset(profile, height, radius, 0, 0));
            Assert.Equal(0, LodCrownProxy.TopOffset(profile, height, radius, radius + 1, 0));
            Assert.Equal(0, LodCrownProxy.TopOffset(profile, height, radius, 0, -(radius + 1)));
            if (radius > 0)
                Assert.Equal(0, LodCrownProxy.TopOffset(profile, height, radius, radius, radius));

            foreach (var (ux, uz) in new[] { (1, 0), (0, 1), (1, 1), (-1, 0), (0, -1), (-1, -1) })
            {
                var previous = height;
                for (var d = 0; d <= radius + 2; d++)
                {
                    var offset = LodCrownProxy.TopOffset(profile, height, radius, ux * d, uz * d);
                    Assert.True(offset >= 0, $"{profile} ({height}, {radius}) went negative at {d}");
                    Assert.True(offset <= height, $"{profile} ({height}, {radius}) rose above its height at {d}");
                    Assert.True(offset <= previous,
                        $"{profile} ({height}, {radius}) climbed from {previous} to {offset} at {d} along ({ux}, {uz})");
                    previous = offset;
                }
            }
        }
    }

    [Fact]
    public void CrownRollIsDeterministicAndInRange()
    {
        var rolls = new Dictionary<TreeSpecies, List<int>>();
        foreach (var (cx, cz) in SampleChunkColumns())
        foreach (var trunk in LeafyTrunksAround(cx, cz, 0))
        {
            var species = trunk.Species;
            var height = TreeGenerator.CrownHeightOf(species, trunk.X, trunk.Z);
            Assert.Equal(height, TreeGenerator.CrownHeightOf(species, trunk.X, trunk.Z));
            Assert.InRange(height, species.CrownHeightMin, species.CrownHeightMax);
            if (!rolls.TryGetValue(species, out var seen))
                rolls[species] = seen = [];
            seen.Add(height);
        }

        Assert.NotEmpty(rolls);
        // A handful of trunks of a species with a real range must not all roll one height
        foreach (var (species, seen) in rolls)
            if (species.CrownHeightMax > species.CrownHeightMin && seen.Count >= 6)
                Assert.True(seen.Distinct().Count() >= 2,
                    $"{species.Name}: all {seen.Count} sampled trunks rolled the same crown height");
    }

    [Fact]
    public void FarRingsNeverRunGrowth()
    {
        // Far from every other fact's search area, so no parallel test fills these caches
        var (cx, cz) = TreeWorldgenFixture.FindForestTreeColumn(origin: (20000, -30000), maxRadius: 96);
        var x = cx * Chunk.Size + 8;
        var z = cz * Chunk.Size + 8;
        static int DivFloor(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

        var leafTiles = new HashSet<ushort>();
        foreach (var species in ProxySpecies)
            leafTiles.UnionWith(TestContent.WorldTiles(BlockType.ById[species.LeavesId]!));

        TreeGenerator.ResetCaches();
        var before = TreeGenerator.GrowsOnThisThread;
        var proxyRings = 0;
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var spec = LodRegion.Levels[level];
            if (!spec.ProxyCanopy) continue;
            proxyRings++;
            var mesh = LodRegion.BuildMeshData(level, DivFloor(x, spec.RegionBlocks), DivFloor(z, spec.RegionBlocks));
            Assert.True(before == TreeGenerator.GrowsOnThisThread, $"LOD ring {level} reads the proxy but grew a tree");
            Assert.Contains(mesh.Vertices, v => (v.PackedColor >> 24) == 253u && leafTiles.Contains(v.Layer));
        }
        Assert.True(proxyRings > 0, "no LOD ring reads the proxy canopy");

        // BuildMeshData runs on the calling thread, so the first ring's grown canopy shows here
        var first = LodRegion.Levels[0];
        Assert.False(first.ProxyCanopy, "the ring meeting the full-detail chunks must read the grown canopy");
        LodRegion.BuildMeshData(0, DivFloor(x, first.RegionBlocks), DivFloor(z, first.RegionBlocks));
        Assert.True(TreeGenerator.GrowsOnThisThread > before, "the first ring read the grown canopy without growing a tree");
    }
}
