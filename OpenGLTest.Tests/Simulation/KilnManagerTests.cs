using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Rendering;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

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

    /// <summary>Every kiln anchor: the 2x2x2 kiln and the 3x3x3 alloy furnace.</summary>
    public static TheoryData<string> KilnAnchors => new() { "mud_brick_kiln", "alloy_furnace" };

    private static (ChunkManager Manager, KilnManager Kilns, DropManager Drops) ConvertedKiln(
        Vector3 anchor, int facing = 0, BlockType? kiln = null)
    {
        kiln ??= Kiln;
        var (manager, _) = MeshTestWorld.WalledArea();
        Multiblock.Convert(manager, anchor, facing, kiln, new HashSet<Vector3>());
        var kilns = new KilnManager();
        kilns.Ensure(anchor, facing, kiln.Id);
        return (manager, kilns, new DropManager());
    }

    private static void AddFuel(KilnManager kilns, Vector3 anchor, int units)
    {
        for (var i = 0; i < units; i++)
            Assert.True(kilns.TryAddFuel(anchor, Coal));
    }

    private static void SetCell(ChunkManager manager, Vector3 cell, ushort id, byte state)
    {
        var chunk = manager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z);
        Assert.NotNull(chunk);
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)cell.X, (int)cell.Y, (int)cell.Z);
        chunk!.SetBlockRaw(lx, ly, lz, id, state);
    }

    private static (ushort Id, byte State) Cell(ChunkManager manager, Vector3 cell)
    {
        Assert.True(manager.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out var state));
        return (id, state);
    }

    [Fact]
    public void EachKilnHoldsAsMuchFuelAsItsOwnProfileAllows()
    {
        foreach (var cap in new[] { KilnProfile.MinFuelCap, KilnProfile.Default.FuelCap * 2 })
        {
            var anchor = new Vector3(2, 2, 2);
            var (_, kilns, _) = ConvertedKiln(anchor);
            var state = kilns.StateAt(2, 2, 2)!;
            state.Profile = KilnProfile.Create(cap, KilnProfile.Default.MeltSpeed, false);

            var taken = 0;
            while (taken <= KilnProfile.MaxFuelCap && kilns.TryAddFuel(anchor, Coal))
                taken++;
            Assert.Equal(cap, taken);
            Assert.Equal(cap, state.Fuel);
        }
    }

    [Fact]
    public void LumberBurnsAsKilnFuelLikeCoal()
    {
        var lumber = ContentRegistry.RequireItem("lumber");
        Assert.True(lumber.FuelSeconds > 0);

        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, _) = ConvertedKiln(anchor);
        Assert.True(kilns.TryAddFuel(anchor, lumber));
        Assert.Equal(lumber.Id, kilns.StateAt(2, 2, 2)!.FuelItemId);
    }

    [Fact]
    public void FuelCapsAtTheProfileAndRefusesNonFuel()
    {
        Assert.Equal(60f, Coal.FuelSeconds);

        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, _) = ConvertedKiln(anchor);

        for (var i = 0; i < Kiln.Kiln!.FuelCap; i++)
            Assert.True(kilns.TryAddFuel(anchor, Coal));
        Assert.False(kilns.TryAddFuel(anchor, Coal));

        // A branch is not fuel, whatever the hearth holds
        Assert.False(kilns.TryAddFuel(anchor, ContentRegistry.RequireItem("branch")));

        var state = kilns.StateAt(2, 2, 2);
        Assert.NotNull(state);
        Assert.Equal(Kiln.Kiln!.FuelCap, state!.Fuel);
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

    [Theory]
    [MemberData(nameof(KilnAnchors))]
    public void BlastedPartRevertsTheRestAndDropsFuel(string kilnName)
    {
        var kiln = ContentRegistry.RequireBlock(kilnName);
        var size = kiln.MultiblockSize;
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops) = ConvertedKiln(anchor, kiln: kiln);
        AddFuel(kilns, anchor, 2);

        // A corner part, on the shell of every size
        var blasted = anchor + new Vector3(size - 1, size - 1, size - 1);
        SetCell(manager, blasted, 0, 0);

        kilns.Update(0.1f, manager, drops);

        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            var (id, state) = Cell(manager, cell);
            // The blasted cell stays as the blast left it, and a revert leaves the interior empty
            if (cell == blasted || Multiblock.IsInterior(dx, dy, dz, size))
            {
                Assert.Equal(0, id);
                continue;
            }
            Assert.Equal(kiln.MultiblockSourceId, id);
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
        foreach (var (dx, dy, dz) in Multiblock.Offsets(Kiln.MultiblockSize))
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
        AddFuel(kilns, anchor, Kiln.Kiln!.FuelCap);
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

    [Theory]
    [MemberData(nameof(KilnAnchors))]
    public void FireSitsInsideTheOpeningForEveryFacing(string kilnName)
    {
        var kiln = ContentRegistry.RequireBlock(kilnName);
        var layout = kiln.Kiln!.Layout;
        var anchor = new Vector3(2, 2, 2);
        for (var facing = 0; facing < 4; facing++)
        {
            var (_, kilns, _) = ConvertedKiln(anchor, facing, kiln);
            AddFuel(kilns, anchor, 1);
            Assert.True(kilns.Ignite(anchor));

            // The list entry is the flame minus the particle offset, so the system's own
            // "cell + SpawnOffset" lands exactly on the layout's FireLocal rotated to this facing
            var flame = kilns.LitCells[0] + FireParticleSystem.SpawnOffset - anchor;
            var expected = MultiblockShape.RotatePoint(layout.FireLocal, facing, kiln.MultiblockSize);
            Assert.Equal(expected.X, flame.X, precision: 4);
            Assert.Equal(expected.Y, flame.Y, precision: 4);
            Assert.Equal(expected.Z, flame.Z, precision: 4);

            // Inside the structure's footprint, standing on the hearth floor under the lintel
            Assert.InRange(flame.X, 0f, (float)kiln.MultiblockSize);
            Assert.InRange(flame.Z, 0f, (float)kiln.MultiblockSize);
            Assert.InRange(flame.Y, layout.OpeningFloorY, layout.OpeningCeilingY);

            // The point light rides 0.65 higher and still clears the lintel
            var light = flame + (RenderManager.FireLightOffset - FireParticleSystem.SpawnOffset);
            Assert.True(light.Y < layout.OpeningCeilingY);
        }
    }
}
