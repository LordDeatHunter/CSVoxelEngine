using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MudTests
{
    public MudTests() => TestContent.EnsureLoaded();

    private static ChunkManager DirtBasin(out byte dirt, out byte mud, out byte water)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        dirt = ContentRegistry.RequireBlock("dirt").Id;
        mud = ContentRegistry.RequireBlock("mud").Id;
        water = ContentRegistry.RequireBlock("water").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(8, 1, 8, dirt, 0);
        return manager;
    }

    private static void SetWater(ChunkManager manager, byte water, bool present)
    {
        var chunk = manager.Chunks[Vector3.Zero];
        for (var x = 7; x <= 9; x++)
        for (var z = 7; z <= 9; z++)
            chunk.SetBlockRaw(x, 2, z, present ? water : (byte)0, present ? FluidState.Source : (byte)0);
    }

    private static void ScheduleWater(FluidManager fluids)
    {
        for (var x = 7; x <= 9; x++)
        for (var z = 7; z <= 9; z++)
            fluids.ScheduleAround(x, 2, z);
    }

    private static byte Cell(ChunkManager manager, int x, int y, int z)
    {
        Assert.True(manager.TryGetCell(x, y, z, out var id, out _));
        return id;
    }

    [Fact]
    public void MudIsASolidFullCubeLikeDirt()
    {
        var mud = ContentRegistry.RequireBlock("mud");
        var dirt = ContentRegistry.RequireBlock("dirt");
        Assert.True(mud.Solid);
        Assert.True(mud.OccludesNeighbours);
        Assert.True(mud.CastsAmbientOcclusion);
        Assert.False(mud.Fluid);
        Assert.True(mud.HasItem);
        Assert.Equal(dirt.MaxHealth, mud.MaxHealth);
    }

    [Fact]
    public void MudDropsExactlyFourMudballs()
    {
        var mud = ContentRegistry.RequireBlock("mud").Id;
        var mudball = ContentRegistry.RequireItem("mudball").Id;
        var into = new List<byte>();
        for (var i = 0; i < 5; i++)
        {
            into.Clear();
            DropManager.RollLoot(mud, new LootContext(0, 0, 0), into);
            Assert.Equal(new[] { mudball, mudball, mudball, mudball }, into);
        }
    }

    [Fact]
    public void FourMudballsCraftOneMud()
    {
        var mud = ContentRegistry.RequireBlock("mud").Id;
        var mudball = ContentRegistry.RequireItem("mudball").Id;
        var recipe = ContentRegistry.RecipeForResult(mud);
        Assert.NotNull(recipe);
        Assert.Equal(1, recipe.ResultCount);
        var ingredient = Assert.Single(recipe.Ingredients);
        Assert.Equal(mudball, ingredient.Id);
        Assert.Equal(4, ingredient.Count);

        var inventory = new Inventory();
        var before = inventory.CountFor(mud);
        Assert.True(inventory.Add(mudball, 4));
        Assert.True(inventory.CanCraft(recipe));
        Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(0, inventory.CountFor(mudball));
        Assert.Equal(before + 1, inventory.CountFor(mud));
    }

    [Fact]
    public void SubmergedDirtConvertsOnTheFirstTick()
    {
        var manager = DirtBasin(out _, out var mud, out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        SetWater(manager, water, true);
        ScheduleWater(fluids);

        fluids.Update(WorldGenerationConfig.FluidTickSeconds, manager, drops);
        Assert.Equal(mud, Cell(manager, 8, 1, 8));
    }

    [Fact]
    public void DirtPlacedAgainstSettledWaterConverts()
    {
        var manager = DirtBasin(out var dirt, out var mud, out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, 0, 0);
        SetWater(manager, water, true);
        ScheduleWater(fluids);
        fluids.Update(1f, manager, drops);
        fluids.Update(1f, manager, drops);

        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, dirt, 0);
        fluids.ScheduleAround(8, 1, 8);
        fluids.Update(WorldGenerationConfig.FluidTickSeconds, manager, drops);
        Assert.Equal(mud, Cell(manager, 8, 1, 8));
    }

    [Fact]
    public void SingleWaterCellNeverConvertsDirt()
    {
        var manager = DirtBasin(out var dirt, out _, out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        var chunk = manager.Chunks[Vector3.Zero];
        chunk.SetBlockRaw(8, 2, 8, water, FluidState.Source);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            chunk.SetBlockRaw(8 + dx, 1, 8 + dz, stone, 0);
            chunk.SetBlockRaw(8 + dx, 2, 8 + dz, stone, 0);
        }
        fluids.ScheduleAround(8, 2, 8);

        for (var i = 0; i < 4; i++)
            fluids.Update(1f, manager, drops);
        Assert.Equal(dirt, Cell(manager, 8, 1, 8));
    }

    [Fact]
    public void DirtBesideWaterButOpenToAirNeverConverts()
    {
        var manager = DirtBasin(out var dirt, out _, out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        var chunk = manager.Chunks[Vector3.Zero];
        chunk.SetBlockRaw(9, 1, 8, water, FluidState.Source);
        chunk.SetBlockRaw(9, 2, 8, water, FluidState.Source);
        fluids.ScheduleAround(9, 1, 8);

        for (var i = 0; i < 4; i++)
            fluids.Update(1f, manager, drops);
        Assert.Equal(dirt, Cell(manager, 8, 1, 8));
    }

    [Fact]
    public void MudStaysMudAfterDrying()
    {
        var manager = DirtBasin(out _, out var mud, out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        SetWater(manager, water, true);
        ScheduleWater(fluids);
        fluids.Update(WorldGenerationConfig.FluidTickSeconds, manager, drops);
        Assert.Equal(mud, Cell(manager, 8, 1, 8));

        SetWater(manager, water, false);
        ScheduleWater(fluids);
        fluids.Update(1f, manager, drops);
        Assert.Equal(mud, Cell(manager, 8, 1, 8));
    }

    private static ChunkManager SubmergedStack(out byte dirt, out byte mud, out byte water,
        out int centerX, out int centerY, out int centerZ)
    {
        dirt = ContentRegistry.RequireBlock("dirt").Id;
        mud = ContentRegistry.RequireBlock("mud").Id;
        water = ContentRegistry.RequireBlock("water").Id;

        centerX = 0;
        centerY = -1;
        centerZ = 0;
        var found = false;
        for (var r = 0; r <= 24 && !found; r++)
        for (var cx = -r; cx <= r && !found; cx++)
        for (var cz = -r; cz <= r && !found; cz++)
        {
            if (cx != -r && cx != r && cz != -r && cz != r) continue;
            var column = Chunk.GetColumnData(cx, cz);
            for (var i = 0; i < column.Heights.Length && !found; i++)
            {
                var h = column.Heights[i];
                if (h < WorldGenerationConfig.SeaLevel - WorldGenerationConfig.BeachBelowSeaLevel
                    && column.DirtDepth[i] > 0)
                {
                    found = true;
                    centerX = cx;
                    centerY = h >> Chunk.SizeShift;
                    centerZ = cz;
                }
            }
        }
        Assert.True(found, "No muddy sea floor near the origin.");

        var manager = new ChunkManager(null!);
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            manager.QueueChunk(new Vector3(centerX + dx, centerY, centerZ + dz));
        return manager;
    }

    private static int CountWaterAround(ChunkManager manager, int x, int y, int z, byte water)
    {
        var count = 0;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            if (manager.GetBlockGlobal(x + dx, y + dy, z + dz) == water) count++;
        }
        return count;
    }

    [Fact]
    public void WorldgenSpawnsWaterTouchingDirtAsMud()
    {
        var manager = SubmergedStack(out var dirt, out var mud, out var water,
            out var cx, out var cy, out var cz);
        var mudCount = 0;
        var dirtCount = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = cx * Chunk.Size + x;
            var wy = cy * Chunk.Size + y;
            var wz = cz * Chunk.Size + z;
            var id = manager.GetBlockGlobal(wx, wy, wz);
            if (id == mud)
            {
                mudCount++;
                if (y > 0 && y < Chunk.Size - 1)
                    Assert.True(CountWaterAround(manager, wx, wy, wz, water) > 0);
            }
            else if (id == dirt)
            {
                dirtCount++;
            }
        }
        Assert.True(mudCount > 0, "No mud spawned on the sea floor.");
    }

    [Fact]
    public void WorldgenTurnsAllInteriorWaterTouchingDirtToMud()
    {
        var manager = SubmergedStack(out var dirt, out _, out var water,
            out var cx, out var cy, out var cz);
        var chunk = manager.Chunks[new Vector3(cx, cy, cz)];
        for (var x = 1; x < Chunk.Size - 1; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 1; z < Chunk.Size - 1; z++)
        {
            if (chunk.GetLocalBlock(x, y, z) != dirt) continue;
            Assert.Equal(0, CountWaterAround(manager,
                cx * Chunk.Size + x, cy * Chunk.Size + y, cz * Chunk.Size + z, water));
        }
    }
}
