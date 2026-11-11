using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Particles;
using VoxelEngine.Particles.Collapse;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Particles.Collapse;

/// <summary>
/// A defeated tower's collapse show (<see cref="TowerCollapseEffects"/>): which cell writes it plays for, and that the blocks
/// it throws off (<see cref="CollapseDebris"/>) are only a picture: each bursts into rubble chips when its life runs out,
/// and none ever writes a cell.
/// </summary>
public sealed class TowerCollapseEffectsTests
{
    public TowerCollapseEffectsTests() => TestContent.EnsureLoaded();

    private const int CenterX = 8;
    private const int CenterZ = 8;
    private const int Radius = 6;
    private const float Frame = 1f / 60f;

    private static ushort Bricks => ContentRegistry.RequireBlock("tower_bricks").Id;
    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;

    // A stone floor at y 0, the camera beside one tracked tower in the given altar state, and the caps filled
    private static (TowerCollapseEffects Effects, BlockDebrisSystem Chips, ChunkManager Chunks) Setup(TowerAltarState altar)
    {
        var (chunks, center) = MeshTestWorld.WalledArea();
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, Stone, 0);
        var towers = new TowerManager(null);
        towers.Track(TowerRecord.Mirror((0, 0), CenterX, CenterZ, Radius, [(0, 12, 0, 0, true)], altar));
        var camera = new Camera(null, null, new Vector3(CenterX, 6f, CenterZ - 12f));
        var chips = new BlockDebrisSystem();
        var effects = new TowerCollapseEffects(camera, chips, chunks, towers, new Random(5));
        effects.Update(0.5f);
        return (effects, chips, chunks);
    }

    [Fact]
    public void OnlyAnUnbreakableBlockGivingWayShowsAsTheCollapse()
    {
        Assert.True(TowerCollapseEffects.ShowsCollapse(Bricks, BlockType.Air));
        Assert.True(TowerCollapseEffects.ShowsCollapse(Bricks, Stone));
        Assert.False(TowerCollapseEffects.ShowsCollapse(Bricks, Bricks));
        Assert.False(TowerCollapseEffects.ShowsCollapse(Stone, BlockType.Air));
        Assert.False(TowerCollapseEffects.ShowsCollapse(BlockType.Air, Stone));
        // A see-through unbreakable block (the roof dome's forcefield) has no rubble to throw: it winks out
        var field = ContentRegistry.RequireBlock("tower_forcefield").Id;
        Assert.True(BlockType.IsUnbreakable(field));
        Assert.False(TowerCollapseEffects.ShowsCollapse(field, BlockType.Air));
    }

    [Fact]
    public void ADefeatedTowersWallGivingWayCrumblesAndThrowsBlocksWhileAnythingElseShowsNothing()
    {
        var wallX = CenterX + Radius - 1;

        var (standing, standingChips, _) = Setup(TowerAltarState.Awake);
        standing.OnCellChanged(wallX, 6, CenterZ, Bricks, BlockType.Air);
        Assert.Empty(standingChips.Debris);
        Assert.Empty(standing.Debris);

        var (effects, chips, chunks) = Setup(TowerAltarState.Defeated);
        effects.OnCellChanged(wallX, 6, CenterZ, Stone, BlockType.Air);
        effects.OnCellChanged(CenterX + 40, 6, CenterZ, Bricks, BlockType.Air);
        Assert.Empty(chips.Debris);
        Assert.Empty(effects.Debris);

        // A cell with brick still standing outside it crumbles, but throws no block that could not clear the wall
        chunks.Chunks[Vector3.Zero].SetBlockRaw(wallX + 1, 9, CenterZ, Bricks, 0);
        effects.OnCellChanged(wallX, 9, CenterZ, Bricks, BlockType.Air);
        Assert.NotEmpty(chips.Debris);
        Assert.Empty(effects.Debris);

        for (var y = 3; y < 9; y++)
            effects.OnCellChanged(wallX, y, CenterZ, Bricks, BlockType.Air);
        Assert.NotEmpty(chips.Debris);
        Assert.All(chips.Debris, chip => Assert.Equal(Bricks, chip.BlockId));
        Assert.NotEmpty(effects.Debris);
        Assert.All(effects.Debris, block => Assert.Equal(Bricks, block.BlockId));
    }

    [Fact]
    public void EveryThrownBlockBurstsIntoRubbleAtTheEndOfItsLifeAndNeverWritesACell()
    {
        var (effects, chips, chunks) = Setup(TowerAltarState.Defeated);
        for (var y = 3; y < 9; y++)
            effects.OnCellChanged(CenterX + Radius - 1, y, CenterZ, Bricks, BlockType.Air);
        Assert.NotEmpty(effects.Debris);
        var before = Snapshot(chunks);

        var frames = (int)((JuiceConfig.TowerDebrisLifetimeMax + 1f) / Frame);
        for (var i = 0; i < frames && effects.Debris.Count > 0; i++)
        {
            var alive = effects.Debris.Count;
            var chipCount = chips.Debris.Count;
            effects.Update(Frame);
            if (effects.Debris.Count < alive)
                Assert.True(chips.Debris.Count > chipCount, "a thrown block ended without a burst of rubble");
        }

        Assert.Empty(effects.Debris);
        Assert.All(chips.Debris, chip => Assert.Equal(Bricks, chip.BlockId));
        Assert.Equal(before, Snapshot(chunks));
    }

    // Every loaded cell's block and state, in a fixed order
    private static List<(ushort Id, byte State)> Snapshot(ChunkManager chunks)
    {
        var cells = new List<(ushort, byte)>();
        foreach (var chunk in chunks.Chunks.Values)
        {
            var origin = chunk.Position * Chunk.Size;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                chunks.TryGetCell((int)origin.X + x, (int)origin.Y + y, (int)origin.Z + z, out var id, out var state);
                cells.Add((id, state));
            }
        }
        return cells;
    }
}
