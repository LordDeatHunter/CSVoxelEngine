using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// <c>support: "above"</c>: a hanging block (the lantern) is held up by the cell over it, so it is
/// placed only where a solid block stands above the target cell, normally against a block's
/// underside, and pops with its loot when that block goes.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HangingBlockTests : IDisposable
{
    private SupportTestWorld? _world;

    public HangingBlockTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.Dispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static BlockType[] Hanging() =>
        BlockType.Types.Values.Where(t => t.NeedsSupport && t.SupportAbove).ToArray();

    [Fact]
    public void AHangingBlockIsHeldUpByTheCellAboveItAndNoOther()
    {
        var hanging = Hanging();
        Assert.NotEmpty(hanging);
        var cell = new Vector3(3, 7, -5);
        foreach (var type in hanging)
        {
            Assert.True(BlockSupport.TryGetSupportCell(type.Id, 0, cell, out var support));
            Assert.Equal(cell + Vector3.UnitY, support);
            for (var face = 0; face < 6; face++)
            {
                var neighbor = cell + Chunk.CubeFaces[face].normal;
                Assert.Equal(neighbor == support, BlockSupport.IsHeldUpBy(type.Id, 0, cell, neighbor));
            }
        }
    }

    [Fact]
    public void TheRegistryReadsSupportAboveWithoutAFacingButOnlyOnAFragileBlock()
    {
        const string path = "data/blocks/x.json";
        var above = new BlockDefinition { Support = "above" };
        Assert.Equal((false, false), ContentRegistry.ParseFacingAndSupport(above, BlockRenderer.Model,
            fluid: false, falls: false, stackMax: 0, needsSupport: true, path, out var supportAbove));
        Assert.True(supportAbove);

        ContentRegistry.ParseFacingAndSupport(new BlockDefinition(), BlockRenderer.Model, false, false, 0, true, path,
            out supportAbove);
        Assert.False(supportAbove);

        // Support means nothing on a block that never pops
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ParseFacingAndSupport(above, BlockRenderer.Model, false, false, 0, needsSupport: false, path,
                out _)).Message);
    }

    [Fact]
    public void AHangingBlockGoesUnderACeilingAndPopsWithIt()
    {
        _world = new SupportTestWorld();
        var lantern = Hanging().First().Id;
        var ground = _world.Top;
        var (x, z) = (4, 6);
        _world.Load(x, ground + 3, z);

        // On the floor there is nothing overhead to hang from: refused, and nothing is spent
        var held = _world.Player.Inventory.CountFor(lantern);
        Assert.False(_world.Place(lantern, new Vector3(x, ground, z), Vector3.UnitY));
        Assert.Equal(held + 1, _world.Player.Inventory.CountFor(lantern));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 1, z));

        // Against a ceiling's underside it hangs in the cell below the ceiling
        var ceiling = (X: x, Y: ground + 3, Z: z);
        _world.Set(ceiling.X, ceiling.Y, ceiling.Z, _world.Stone);
        Assert.True(_world.Place(lantern, new Vector3(ceiling.X, ceiling.Y, ceiling.Z), -Vector3.UnitY));
        Assert.Equal(lantern, _world.IdAt(x, ceiling.Y - 1, z));

        // The floor under it holds nothing up
        Assert.True(_world.Break(x, ground, z));
        Assert.Equal(lantern, _world.IdAt(x, ceiling.Y - 1, z));

        // The ceiling takes it down
        Assert.True(_world.Break(ceiling.X, ceiling.Y, ceiling.Z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ceiling.Y - 1, z));
    }
}
