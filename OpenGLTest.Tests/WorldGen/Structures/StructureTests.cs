using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Structures;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Structures;

/// <summary>
/// The structure template system and the ruins built on it: a template turns without losing or moving a cell wrongly (and
/// its facings turn with it), a bad file is refused naming itself, a footprint crossing chunk borders stamps the same cells
/// whichever chunk draws them, and every placement finds sites that obey its anchor rule.
/// </summary>
[Collection("Worldgen seed")]
public sealed class StructureTests
{
    public StructureTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static StructureDefinition Definition(string[][] layers, string kind = "surface") => new()
    {
        Kind = kind,
        Palette = new Dictionary<string, JsonElement>
        {
            ["s"] = Json("\"stone\""),
            ["t"] = Json("{\"block\": \"oak_planks_stairs\", \"facing\": \"north\"}"),
        },
        Layers = layers,
    };

    private static StructureTemplate Compile(string[][] layers, string kind = "surface") =>
        StructureLoader.Compile("test", Definition(layers, kind), "test.json");

    // A definition whose palette also holds a waystone (w) and its upper half (u)
    private static StructureDefinition WaystoneDefinition(string[][] layers)
    {
        var def = Definition(layers);
        def.Palette!["w"] = Json("\"waystone_surface\"");
        def.Palette!["u"] = Json("\"waystone_surface_upper\"");
        return def;
    }

    [Fact]
    public void ASatelliteHoldsOneWholeWaystoneEveryTurn()
    {
        var def = WaystoneDefinition([["sw", "ss"], ["su", "ss"]]);
        def.Satellite = true;
        var template = StructureLoader.Compile("test", def, "test.json");
        var lower = ContentRegistry.RequireBlock("waystone_surface").Id;
        for (var turns = 0; turns < 4; turns++)
        {
            var cells = Enumerable.Range(0, 4).Where(i => (template.CellAt(turns, i % 2, 0, i / 2) & 0xFF) == lower).ToArray();
            Assert.Single(cells);
            var (x, z) = (cells[0] % 2, cells[0] / 2);
            Assert.Equal(BlockType.ById[lower]!.UpperHalfId, template.CellAt(turns, x, 1, z) & 0xFF);
        }
    }

    [Fact]
    public void ATurnMovesEachCellClockwiseAndTurnsItsFacingWithIt()
    {
        var template = Compile([["t..", "...", "..s"]]);
        var stair = ContentRegistry.RequireBlock("oak_planks_stairs").Id;

        // North-west corner, facing north: one turn puts it in the north-east corner facing east
        Assert.Equal(stair | (0 << 8), template.CellAt(0, 0, 0, 0));
        Assert.Equal(stair | (StructureTemplate.TurnedFacing[0] << 8), template.CellAt(1, 2, 0, 0));
        // The facing walks north, east, south, west across the turns
        var seen = new[] { 0, 1, 2, 3 }.Select(turns => FacingOf(template, turns, stair)).ToArray();
        Assert.Equal(4, seen.Distinct().Count());
        Assert.Equal(0, seen[0]);
    }

    private static int FacingOf(StructureTemplate template, int turns, byte stair)
    {
        for (var z = 0; z < template.Size; z++)
        for (var x = 0; x < template.Size; x++)
            if ((template.CellAt(turns, x, 0, z) & 0xFF) == stair)
                return (template.CellAt(turns, x, 0, z) >> 8) & 3;
        throw new InvalidOperationException("the stair vanished");
    }

    [Fact]
    public void EveryTurnKeepsEveryCell()
    {
        foreach (var template in StructureRegistry.All)
        {
            var counts = Enumerable.Range(0, 4).Select(turns => Histogram(template, turns)).ToArray();
            for (var turns = 1; turns < 4; turns++)
                Assert.True(counts[0].SequenceEqual(counts[turns]), $"{template.Name} changes its cells when turned {turns} times");
        }
    }

    private static int[] Histogram(StructureTemplate template, int turns)
    {
        var counts = new int[257];
        for (var y = 0; y < template.Height; y++)
        for (var z = 0; z < template.Size; z++)
        for (var x = 0; x < template.Size; x++)
        {
            var cell = template.CellAt(turns, x, y, z);
            counts[cell < 0 ? 256 : cell & 0xFF]++;
        }
        return counts;
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("palette")]
    [InlineData("ragged")]
    [InlineData("facing")]
    [InlineData("missingBlock")]
    [InlineData("satelliteWithoutWaystone")]
    [InlineData("satelliteWithTwoWaystones")]
    [InlineData("ruinWithWaystone")]
    [InlineData("waystoneWithoutItsUpperHalf")]
    [InlineData("upperHalfWithoutItsWaystone")]
    public void ABadFileIsRefusedAndNamed(string what)
    {
        var def = WaystoneDefinition([["ss", "ss"]]);
        switch (what)
        {
            case "satelliteWithoutWaystone": def.Satellite = true; break;
            case "satelliteWithTwoWaystones": def.Satellite = true; def.Layers = [["sw", "ws"], ["su", "us"]]; break;
            case "ruinWithWaystone": def.Layers = [["sw", "ss"], ["su", "ss"]]; break;
            case "waystoneWithoutItsUpperHalf": def.Satellite = true; def.Layers = [["sw", "ss"], ["ss", "ss"]]; break;
            case "upperHalfWithoutItsWaystone": def.Satellite = true; def.Layers = [["sw", "ss"], ["su", "su"]]; break;
            case "kind": def.Kind = "castle"; break;
            case "palette": def.Layers = [["sx", "ss"]]; break;
            case "ragged": def.Layers = [["ss", "s"]]; break;
            case "facing": def.Palette!["s"] = Json("{\"block\": \"stone\", \"facing\": \"north\"}"); break;
            case "missingBlock": def.Palette!["s"] = Json("\"no_such_block\""); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => StructureLoader.Compile("test", def, "bad/test.json"));
        Assert.Contains("bad/test.json", error.Message);
    }

    [Fact]
    public void EveryRuinKindHasTemplatesThatHoldTheirOwnLootChest()
    {
        foreach (var kind in new[] { StructureKind.Surface, StructureKind.Underground, StructureKind.Hell, StructureKind.Sky })
        {
            var chest = ContentRegistry.RequireBlock($"ruin_chest_{kind.ToString().ToLowerInvariant()}").Id;
            Assert.NotEmpty(StructureRegistry.Of(kind));
            foreach (var template in StructureRegistry.Of(kind))
                Assert.True(Histogram(template, 0)[chest] > 0, $"{template.Name} holds no {kind} loot chest");
        }
    }

    [Fact]
    public void AFootprintCrossingChunkBordersStampsTheSameCellsWhicheverChunkDrawsThem()
    {
        var template = StructureRegistry.All.First(t => t.Size >= 7);
        const int originX = -3, originY = 2, originZ = -4;
        for (var turns = 0; turns < 4; turns++)
        {
            var written = new Dictionary<(int, int, int), int>();
            foreach (var (cx, cz) in new[] { (-16, -16), (-16, 0), (0, -16), (0, 0) })
            {
                var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
                byte[]? state = null;
                StructureStamper.Stamp(blocks, ref state, cx, 0, cz, template, turns, originX, originY, originZ, int.MinValue, int.MaxValue);
                for (var lx = 0; lx < Chunk.Size; lx++)
                for (var ly = 0; ly < Chunk.Size; ly++)
                for (var lz = 0; lz < Chunk.Size; lz++)
                {
                    var i = Chunk.Idx(lx, ly, lz);
                    if (blocks[i] == 0 && StateOf(state, i) == 0) continue;
                    written[(cx + lx, ly, cz + lz)] = blocks[i] | (StateOf(state, i) << 8);
                }
            }

            for (var y = 0; y < template.Height; y++)
            for (var z = 0; z < template.Size; z++)
            for (var x = 0; x < template.Size; x++)
            {
                var cell = template.CellAt(turns, x, y, z);
                if (cell <= 0) continue;
                Assert.True(written.TryGetValue((originX + x, originY + y, originZ + z), out var got), $"cell {x},{y},{z} missing at turn {turns}");
                Assert.Equal(cell, got);
            }
        }
    }

    private static int StateOf(byte[]? state, int i) => state?[i] ?? 0;

    [Fact]
    public void TheWindowKeepsACellOutOfALayerItDoesNotOwn()
    {
        var template = Compile([["ss", "ss"], ["ss", "ss"]]);
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        byte[]? state = null;
        StructureStamper.Stamp(blocks, ref state, 0, 0, 0, template, 0, 0, 4, 0, minY: 5, maxY: 5);
        Assert.Equal(0, blocks[Chunk.Idx(0, 4, 0)]);
        Assert.NotEqual(0, blocks[Chunk.Idx(0, 5, 0)]);
    }

    [Fact]
    public void SurfaceSitesStandOnLevelDryGroundAndRepeatExactly()
    {
        var found = 0;
        for (var cx = -40; cx < 40 && found < 3; cx++)
        for (var cz = -40; cz < 40 && found < 3; cz++)
        {
            var x = cx * 53;
            var z = cz * 53;
            var hash = SplitMix64.Hash(cx, cz, 1234);
            if (!RuinPlacement.TryFindSurfaceSite(x, z, hash, out var template, out var ground)) continue;
            found++;
            Assert.True(RuinPlacement.TryFindSurfaceSite(x, z, hash, out var again, out var groundAgain));
            Assert.Same(template, again);
            Assert.Equal(ground, groundAgain);
            Assert.False(TerrainGenerator.IsWaterColumn(x, z, ground));
        }
        Assert.True(found > 0, "no surface site found over a wide scan");
    }

    [Fact]
    public void UndergroundRoomsSitUnderTheirCoverAndOverTheBarrier()
    {
        var found = 0;
        for (var cx = -30; cx < 30 && found < 5; cx++)
        for (var cz = -30; cz < 30 && found < 5; cz++)
        {
            var x = cx * 97;
            var z = cz * 97;
            var hash = SplitMix64.Hash(cx, cz, 99);
            if (!RuinPlacement.TryFindUndergroundSite(x, z, hash, out var template, out var originY)) continue;
            found++;
            var ground = ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
                [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
            Assert.True(originY > WorldGenerationConfig.BarrierTopY);
            Assert.True(originY + template.Height - 1 <= ground - WorldFeatureConfig.RuinUndergroundCover);
        }
        Assert.True(found > 0);
    }

    [Fact]
    public void HellRuinsStandOnASolidPocketFloorInsideTheirLayer()
    {
        var found = 0;
        for (var cx = -40; cx < 40 && found < 2; cx++)
        for (var cz = -40; cz < 40 && found < 2; cz++)
        {
            var x = cx * 41;
            var z = cz * 41;
            const int layer = 1;
            var hash = SplitMix64.Hash(cx, cz, 4321);
            if (!RuinPlacement.TryFindHellSite(x, z, layer, hash, out _, out var floor)) continue;
            found++;
            Assert.Equal(layer, HellGenerator.LayerIndex(floor));
            Assert.NotEqual(BlockType.Air, HellGenerator.BlockAt(x, floor - 1, z));
            Assert.True(HellGenerator.IsCavernAir(x, floor, z, new Dictionary<long, float>()));
        }
        Assert.True(found > 0, "no hell site found over a wide scan");
    }
}
