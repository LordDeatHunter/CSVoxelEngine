using System;
using System.IO;
using System.Linq;
using System.Reflection;
using VoxelEngine.Content.Blocks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Content.Blocks;

public sealed class BlockTablesTests
{
    public BlockTablesTests() => TestContent.EnsureLoaded();

    [Fact]
    public void EveryPerIdTableCoversEveryAssignedId()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var tables = typeof(BlockType).GetFields(flags)
            .Where(f => f.FieldType.IsArray)
            .Select(f => (f.Name, Table: (Array)f.GetValue(null)!))
            .Concat(typeof(BlockType).GetProperties(flags)
                .Where(p => p.PropertyType.IsArray)
                .Select(p => (p.Name, Table: (Array)p.GetValue(null)!)))
            .ToArray();

        Assert.Contains(tables, t => t.Name == nameof(BlockType.ById));
        Assert.Equal(1 + ContentRegistry.BlockIds.Count + ContentRegistry.ItemIds.Count, BlockTables.IdCount);
        Assert.All(tables, t => Assert.True(t.Table.Length == BlockTables.IdCount,
            $"{t.Name} holds {t.Table.Length} entries for {BlockTables.IdCount} ids"));
    }

    [Fact]
    public void AirKeepsItsDefaultsAfterAllocation()
    {
        // Air owns no definition, yet light, flows and placements must treat it as empty space
        // with the flow tuning any block without a flow object gets
        var unflowed = new BlockType { Name = "probe", DisplayName = "probe", Id = BlockType.Air };
        Assert.Null(BlockType.ById[BlockType.Air]);
        Assert.True(BlockType.PassesLightById[BlockType.Air]);
        Assert.True(BlockType.IsReplaceable(BlockType.Air));
        Assert.False(BlockType.IsSolid(BlockType.Air));
        Assert.Equal(0, BlockType.Emission[BlockType.Air]);
        Assert.Equal(unflowed.FluidTickDivisor, BlockType.FluidTickDivisorById[BlockType.Air]);
        Assert.Equal(unflowed.FluidRange, BlockType.FluidRangeById[BlockType.Air]);
        Assert.Equal(unflowed.FluidInfinite, BlockType.FluidInfiniteById[BlockType.Air]);
    }

    [Fact]
    public void AnIdWithoutABlockHasTheDefaultFriction()
    {
        var blockless = ContentRegistry.ItemIds.Values.Append(BlockType.Air).ToArray();
        Assert.True(blockless.Length > 1, "the content has no pure item to check");
        Assert.All(blockless, id =>
        {
            Assert.Null(BlockType.ById[id]);
            Assert.Equal(BlockType.DefaultFriction, BlockType.FrictionOf(id));
        });
    }

    [Fact]
    public void IsAssignedIdBoundsTheRegistry()
    {
        var count = BlockTables.IdCount;
        Assert.False(BlockTables.IsAssignedId(BlockType.Air));
        Assert.False(BlockTables.IsAssignedId(count));
        Assert.False(BlockTables.IsAssignedId(-1));
        Assert.True(BlockTables.IsAssignedId(1));
        Assert.True(BlockTables.IsAssignedId(count - 1));

        Assert.Equal(BlockType.Air, BlockTables.RequireAssigned(0));
        Assert.Equal(count - 1, BlockTables.RequireAssigned(count - 1));
        Assert.Throws<InvalidDataException>(() => BlockTables.RequireAssigned(count));
        Assert.Throws<InvalidDataException>(() => BlockTables.RequireAssigned(-1));
    }

    [Fact]
    public void SkyCoverSearchAgreesWithTheFlagTable()
    {
        Assert.Contains(BlockType.Types.Values, t => t.CoversSky);
        Assert.False(BlockType.ContainsSkyCover(new ushort[Chunk.BlockCount]));

        for (var id = 0; id < BlockTables.IdCount; id++)
        {
            var blocks = new ushort[Chunk.BlockCount];
            blocks[^1] = (ushort)id;
            Assert.True(BlockType.CoversSkyById[id] == BlockType.ContainsSkyCover(blocks),
                BlockType.ById[id]?.Name ?? id.ToString());
        }
    }

    [Fact]
    public void TheTreeAndFallenLogTablesCoverEveryLoadedId()
    {
        // The worldgen classes size their per-id tables at first touch, after the content load:
        // they cover every loaded id, agree with the species data, and read false past the end
        Assert.True(TreeGenerator.LeafById.Length >= BlockTables.IdCount);
        for (var id = 0; id < BlockTables.IdCount; id++)
        {
            TreeGenerator.IsLeaf((ushort)id);
            TreeGenerator.IsTreeBlock((ushort)id);
            FallenLogGenerator.IsFallenLog((ushort)id);
        }

        var leafy = TreeSpeciesRegistry.All.Where(s => s.HasLeaves).ToArray();
        Assert.NotEmpty(leafy);
        Assert.All(leafy, s =>
        {
            Assert.True(TreeGenerator.IsLeaf(s.LeavesId), s.Name);
            Assert.True(TreeGenerator.IsTreeBlock(s.LeavesId), s.Name);
            Assert.True(TreeGenerator.IsTreeBlock(s.WoodId), s.Name);
        });

        var logs = TreeGenerator.SpeciesByBiome
            .Select(s => s?.Worldgen?.FallenLogId ?? BlockType.Air)
            .Where(id => id != BlockType.Air)
            .ToArray();
        Assert.NotEmpty(logs);
        Assert.All(logs, id => Assert.True(FallenLogGenerator.IsFallenLog(id)));

        Assert.True(BlockTables.IdCount <= ushort.MaxValue, "no id lies past the tables to probe");
        var past = (ushort)BlockTables.IdCount;
        Assert.False(TreeGenerator.IsLeaf(past));
        Assert.False(TreeGenerator.IsTreeBlock(past));
        Assert.False(FallenLogGenerator.IsFallenLog(past));
    }
}
