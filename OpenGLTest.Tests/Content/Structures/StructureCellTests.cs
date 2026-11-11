using VoxelEngine.Content.Structures;
using Xunit;

namespace VoxelEngine.Tests.Content.Structures;

/// <summary>
/// Templates, the tower and its ruin hand cells around as one packed int: the block and the state must come back out
/// exactly as they went in, and no written cell may read as "writes nothing".
/// </summary>
public sealed class StructureCellTests
{
    public static TheoryData<int, int> Extremes()
    {
        var data = new TheoryData<int, int>();
        foreach (var block in new[] { 0, 1, (int)ushort.MaxValue })
        foreach (var state in new[] { 0, 1, (int)byte.MaxValue })
            data.Add(block, state);
        return data;
    }

    [Theory]
    [MemberData(nameof(Extremes))]
    public void BlockAndStateRoundTripAtTheirExtremes(int block, int state)
    {
        var cell = StructureCell.Pack((ushort)block, (byte)state);

        Assert.Equal((ushort)block, StructureCell.BlockOf(cell));
        Assert.Equal((byte)state, StructureCell.StateOf(cell));
    }

    // The tower shell and ruin return a bare block id as a cell; that only holds while state 0 packs to the id
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData((int)ushort.MaxValue)]
    public void AStatelessCellIsItsBareId(int block) =>
        Assert.Equal(block, StructureCell.Pack((ushort)block, 0));

    [Theory]
    [MemberData(nameof(Extremes))]
    public void APackedCellIsNeverTheNoCellSentinel(int block, int state)
    {
        var cell = StructureCell.Pack((ushort)block, (byte)state);

        Assert.NotEqual(StructureCell.None, cell);
        // Callers test "writes nothing" with cell < 0, so no packed cell may be negative
        Assert.True(cell >= 0, $"block {block}, state {state} packs to the negative cell {cell}");
    }

    [Theory]
    [MemberData(nameof(Extremes))]
    public void WithBlockKeepsTheState(int block, int state)
    {
        var cell = StructureCell.Pack((ushort)block, (byte)state);
        var other = (ushort)(ushort.MaxValue - block);

        var swapped = StructureCell.WithBlock(cell, other);

        Assert.Equal(other, StructureCell.BlockOf(swapped));
        Assert.Equal((byte)state, StructureCell.StateOf(swapped));
        Assert.Equal(StructureCell.Pack(other, (byte)state), swapped);
    }
}
