using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The kiln's block-entity rules: what it accepts as fuel, how a strike burns through it, where
/// its flame sits inside the hearth for every facing, and the integrity sweep that reverts a
/// structure a blast took a cell out of — the anchor cell included, which nothing else cleans up.
/// </summary>
public sealed class KilnManagerTests
{
    public KilnManagerTests() => TestContent.EnsureLoaded();

    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static BlockType Bricks => ContentRegistry.RequireBlock("mud_bricks");
    private static Item Coal => ContentRegistry.RequireItem("coal");

    private static (ChunkManager Manager, KilnManager Kilns, DropManager Drops) ConvertedKiln(
        Vector3 anchor, int facing = 0)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        Multiblock.Convert(manager, anchor, facing, Kiln, new HashSet<Vector3>());
        var kilns = new KilnManager();
        kilns.Ensure(anchor, facing, Kiln.Id);
        return (manager, kilns, new DropManager());
    }

    private static void AddFuel(KilnManager kilns, Vector3 anchor, int units)
    {
        for (var i = 0; i < units; i++)
            Assert.True(kilns.TryAddFuel(anchor, Coal));
    }

    private static void SetCell(ChunkManager manager, Vector3 cell, byte id, byte state)
    {
        var chunk = manager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z);
        Assert.NotNull(chunk);
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)cell.X, (int)cell.Y, (int)cell.Z);
        chunk!.SetBlockRaw(lx, ly, lz, id, state);
    }

    private static (byte Id, byte State) Cell(ChunkManager manager, Vector3 cell)
    {
        Assert.True(manager.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state));
        return (id, state);
    }

    [Fact]
    public void FuelCapsAtFourAndRefusesNonFuel()
    {
        Assert.Equal(60f, Coal.FuelSeconds);

        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, _) = ConvertedKiln(anchor);

        for (var i = 0; i < KilnManager.MaxFuel; i++)
            Assert.True(kilns.TryAddFuel(anchor, Coal));
        Assert.False(kilns.TryAddFuel(anchor, Coal));

        // A branch is not fuel, whatever the hearth holds
        Assert.False(kilns.TryAddFuel(anchor, ContentRegistry.RequireItem("branch")));

        var state = kilns.StateAt(2, 2, 2);
        Assert.NotNull(state);
        Assert.Equal(KilnManager.MaxFuel, state!.Fuel);
        Assert.Equal(Coal.Id, state.FuelItemId);

        // A kiln nobody registered takes nothing
        Assert.False(kilns.TryAddFuel(new Vector3(9, 9, 9), Coal));
    }

    [Fact]
    public void IgniteNeedsFuelAndBurnsThroughIt()
    {
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops) = ConvertedKiln(anchor);

        Assert.False(kilns.CanIgnite(anchor));
        Assert.False(kilns.Ignite(anchor));

        AddFuel(kilns, anchor, 2);
        Assert.True(kilns.CanIgnite(anchor));
        Assert.True(kilns.Ignite(anchor));

        var state = kilns.StateAt(2, 2, 2)!;
        Assert.True(state.Lit);
        Assert.Equal(1, state.Fuel);
        Assert.Single(kilns.LitCells);

        kilns.Update(59f, manager, drops);
        Assert.True(state.Lit);

        // The second unit is picked up when the first burns out
        kilns.Update(2f, manager, drops);
        Assert.True(state.Lit);
        Assert.Equal(0, state.Fuel);

        kilns.Update(60f, manager, drops);
        Assert.False(state.Lit);
        Assert.Empty(kilns.LitCells);

        // Burning down is not a break: the structure stands and nothing dropped
        Assert.Empty(drops.Drops);
        Assert.Equal(Kiln.Id, Cell(manager, anchor).Id);
    }

    [Fact]
    public void BlastedPartRevertsTheRestAndDropsFuel()
    {
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops) = ConvertedKiln(anchor);
        AddFuel(kilns, anchor, 2);

        var blasted = anchor + new Vector3(1, 1, 1);
        SetCell(manager, blasted, 0, 0);

        kilns.Update(0.1f, manager, drops);

        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            if (cell == blasted)
            {
                Assert.Equal(0, id);
                continue;
            }
            Assert.Equal(Bricks.Id, id);
            Assert.Equal(0, state);
        }

        Assert.Null(kilns.StateAt(2, 2, 2));
        Assert.Equal(2, drops.Drops.Count);
        Assert.All(drops.Drops, d => Assert.Equal(Coal.Id, d.BlockId));
    }

    [Fact]
    public void BlastedAnchorRevertsTheSurvivingParts()
    {
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops) = ConvertedKiln(anchor);
        AddFuel(kilns, anchor, 3);
        Assert.True(kilns.Ignite(anchor));

        SetCell(manager, anchor, 0, 0);

        kilns.Update(0.1f, manager, drops);

        // The anchor's cell is left exactly as the blast left it; only its parts revert
        Assert.Equal(0, Cell(manager, anchor).Id);
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var (id, state) = Cell(manager, anchor + new Vector3(dx, dy, dz));
            Assert.Equal(Bricks.Id, id);
            Assert.Equal(0, state);
        }

        Assert.Null(kilns.StateAt(2, 2, 2));
        Assert.Empty(kilns.LitCells);
        Assert.Equal(2, drops.Drops.Count);
        Assert.All(drops.Drops, d => Assert.Equal(Coal.Id, d.BlockId));
    }

    [Fact]
    public void NotifyBlockRemovedDropsFuelAndForgets()
    {
        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, drops) = ConvertedKiln(anchor);
        AddFuel(kilns, anchor, KilnManager.MaxFuel);
        Assert.True(kilns.Ignite(anchor));
        Assert.Single(kilns.LitCells);

        var mined = anchor + new Vector3(1, 0, 1);
        kilns.NotifyBlockRemoved(anchor, mined, drops);

        Assert.Equal(3, drops.Drops.Count);
        Assert.All(drops.Drops, drop =>
        {
            Assert.Equal(Coal.Id, drop.BlockId);
            Assert.True(drop.Position.X >= mined.X && drop.Position.X <= mined.X + 1f);
            Assert.True(drop.Position.Y >= mined.Y && drop.Position.Y <= mined.Y + 1f);
            Assert.True(drop.Position.Z >= mined.Z && drop.Position.Z <= mined.Z + 1f);
        });
        Assert.Null(kilns.StateAt(2, 2, 2));
        Assert.Empty(kilns.LitCells);
    }

    [Fact]
    public void FireSitsInsideTheOpeningForEveryFacing()
    {
        var anchor = new Vector3(2, 2, 2);
        for (var facing = 0; facing < 4; facing++)
        {
            var (_, kilns, _) = ConvertedKiln(anchor, facing);
            AddFuel(kilns, anchor, 1);
            Assert.True(kilns.Ignite(anchor));

            // The list entry is the flame minus the particle offset, so the system's own
            // "cell + SpawnOffset" lands exactly on FireLocal rotated to this facing
            var flame = kilns.LitCells[0] + FireParticleSystem.SpawnOffset - anchor;
            var expected = MultiblockShape.RotatePoint(KilnRenderer.FireLocal, facing);
            Assert.Equal(expected.X, flame.X, precision: 4);
            Assert.Equal(expected.Y, flame.Y, precision: 4);
            Assert.Equal(expected.Z, flame.Z, precision: 4);

            // Inside the structure's footprint, standing on the hearth floor under the lintel
            Assert.InRange(flame.X, 0f, (float)MultiblockShape.Size);
            Assert.InRange(flame.Z, 0f, (float)MultiblockShape.Size);
            Assert.InRange(flame.Y, KilnRenderer.OpeningFloorY, KilnRenderer.OpeningCeilingY);

            // The point light rides 0.65 higher and still clears the lintel
            var light = flame + (RenderManager.FireLightOffset - FireParticleSystem.SpawnOffset);
            Assert.True(light.Y < KilnRenderer.OpeningCeilingY);
        }
    }
}
