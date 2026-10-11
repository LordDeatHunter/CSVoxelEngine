using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Which cell a right-click fills and what it may write over. A stack that opts in
/// (<c>replaceableByPlacement</c>) is taken over in place while it is not solid, so a block placed
/// against a partial snow drift replaces it; a full stack and every unflagged block keep the
/// ordinary "fill the cell across the hit face" behaviour.
/// </summary>
public sealed class PlacementTests
{
    public PlacementTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void PartialSnowIsTakenOverByAPlacementAndAFullStackIsNot()
    {
        var snow = BlockType.ById[Block("snow")]!;

        Assert.True(BlockType.IsReplaceableByPlacement(snow.Id, 1));
        Assert.True(BlockType.IsReplaceableByPlacement(snow.Id, (byte)(snow.StackMax - 1)));
        Assert.False(BlockType.IsReplaceableByPlacement(snow.Id, snow.StackMax));
    }

    [Fact]
    public void UnflaggedBlocksKeepTheirCell()
    {
        Assert.False(BlockType.IsReplaceableByPlacement(Block("stone"), 0));
        Assert.False(BlockType.IsReplaceableByPlacement(Block("pebble"), 0));
        Assert.False(BlockType.IsReplaceableByPlacement(BlockType.Air, 0));
    }

    [Fact]
    public void PlacementAimedAtAPartialDriftFillsTheDriftCell()
    {
        var snow = Block("snow");
        var full = BlockType.ById[snow]!.StackMax;
        var hit = new Vector3(4, 5, 6);

        Assert.Equal(hit, Placement.TargetCell(hit, Vector3.UnitY, snow, 1));
        Assert.Equal(hit, Placement.TargetCell(hit, Vector3.UnitX, snow, 1));
        Assert.Equal(hit + Vector3.UnitY, Placement.TargetCell(hit, Vector3.UnitY, snow, full));
        Assert.Equal(hit + Vector3.UnitY, Placement.TargetCell(hit, Vector3.UnitY, Block("stone"), 0));
    }

    [Fact]
    public void OnlyAirFluidAndOverridableStacksAcceptAPlacement()
    {
        var snow = Block("snow");
        var full = BlockType.ById[snow]!.StackMax;

        Assert.True(Placement.CanOccupy(BlockType.Air, 0));
        Assert.True(Placement.CanOccupy(Block("water"), 0));
        Assert.True(Placement.CanOccupy(snow, 1));
        Assert.False(Placement.CanOccupy(snow, full));
        Assert.False(Placement.CanOccupy(Block("stone"), 0));
    }

    [Fact]
    public void APlacementTakesOverAPartialDriftCellButNotAFullStack()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        MeshTestWorld.FillWalledArea(manager, Block("stone"));

        var snow = BlockType.ById[Block("snow")]!;
        var dirt = Block("dirt");
        var world = center.Position * Chunk.Size + new Vector3(8, 8, 8);

        center.SetBlockRaw(8, 8, 8, snow.Id, 1);
        Assert.True(center.PlaceBlockAt(world, dirt, manager));
        Assert.Equal(dirt, center.GetLocalBlock(8, 8, 8));

        center.SetBlockRaw(8, 8, 8, snow.Id, snow.StackMax);
        Assert.False(center.PlaceBlockAt(world, dirt, manager));
        Assert.Equal(snow.Id, center.GetLocalBlock(8, 8, 8));
        Assert.Equal(snow.StackMax, center.GetLocalState(8, 8, 8));
    }

    [Fact]
    public void APlacementAimedAtAPlantFillsThePlantCell()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        MeshTestWorld.FillWalledArea(manager, Block("stone"));
        var dirt = Block("dirt");
        var world = center.Position * Chunk.Size + new Vector3(8, 8, 8);
        var hit = new Vector3(4, 5, 6);

        foreach (var name in new[] { "tall_grass", "dead_shrub", "flower", "cotton_plant" })
        {
            var plant = Block(name);
            // The aim fills the plant's own cell instead of the one across the hit face...
            Assert.True(BlockType.IsReplaceableByPlacement(plant, 0));
            Assert.Equal(hit, Placement.TargetCell(hit, Vector3.UnitY, plant, 0));
            // ...and the write takes it over, plant gone
            center.SetBlockRaw(8, 8, 8, plant, 0);
            Assert.True(center.PlaceBlockAt(world, dirt, manager));
            Assert.Equal(dirt, center.GetLocalBlock(8, 8, 8));
        }
    }
}
