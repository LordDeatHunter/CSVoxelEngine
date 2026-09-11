using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>Covers the item-19 slicing of the buried-flag pass: new regions evaluate the
/// same pass they are added in, still-buried regions re-evaluate every pass (a stale
/// buried flag is a horizon hole), and the unburied level-0 slice converges over
/// successive passes instead of testing everything at once.</summary>
public sealed class LodManagerTests
{
    public LodManagerTests() => TestContent.EnsureLoaded();

    private static LodRegion HeadlessRegion(int x, int z, int cy)
    {
        var span = LodRegion.Levels[0].RegionBlocks / Chunk.Size;
        var n = span * span;
        var mins = new int[n];
        var maxs = new int[n];
        Array.Fill(mins, cy);
        Array.Fill(maxs, cy);
        var data = new LodMeshData(0, x, z, [], [], [], [], 0f, 0f, mins, maxs);
        return new LodRegion(null!, data);
    }

    private static void MeshColumns(ChunkManager chunks, int baseCx, int baseCz, int cy)
    {
        var empty = new ChunkMeshData([], [], [], [], false);
        for (var cx = baseCx; cx < baseCx + 4; cx++)
        for (var cz = baseCz; cz < baseCz + 4; cz++)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            chunk.UpdateMesh(empty, null, null);
            chunks.Chunks[pos] = chunk;
        }
    }

    [Fact]
    public void CoverSliceConvergesWithoutStuckShells()
    {
        var chunks = new ChunkManager(null!);
        MeshColumns(chunks, 0, 0, 7);

        var lods = new LodManager(null!);
        // Inserted first so the slice reaches it last: it must still converge.
        var covered = HeadlessRegion(0, 0, 7);
        lods.AddRegion((0, 0, 0), covered);
        var lateCovered = HeadlessRegion(1, 0, 7);
        lods.AddRegion((0, 1, 0), lateCovered);
        var control = HeadlessRegion(4, 4, 7);
        lods.AddRegion((0, 4, 4), control);
        for (var rx = -4; rx <= 4; rx++)
        for (var rz = -4; rz <= 4; rz++)
        {
            if ((rx == 0 && rz == 0) || (rx == 1 && rz == 0) || (rx == 4 && rz == 4))
                continue;
            lods.AddRegion((0, rx, rz), HeadlessRegion(rx, rz, 7));
        }

        // Pass 1: everything is newly added, so everything evaluates at once.
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(covered.IsBuried);
        Assert.False(lateCovered.IsBuried);
        Assert.False(control.IsBuried);

        // Cover the late region, then run one more pass: the 64-wide slice cannot
        // reach it at the head of the list, so it must stay unburied this pass...
        MeshColumns(chunks, 4, 0, 7);
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(covered.IsBuried);
        Assert.False(lateCovered.IsBuried);

        // ...but the unconverged cycle keeps the pass running until it does.
        lods.UpdateCoverFlags(chunks, Vector3.Zero);
        Assert.True(covered.IsBuried);
        Assert.True(lateCovered.IsBuried);
        Assert.False(control.IsBuried);
    }
}
