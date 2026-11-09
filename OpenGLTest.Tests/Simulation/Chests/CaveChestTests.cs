using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Structures;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.WorldGen.Caves;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Chests;

/// <summary>
/// Cave chests (CB-4): an underground ruin chest's tier is the cave biome of its own cell, stamped into its state by the
/// ruin stamp. Every cave biome picks a loaded table of its own, the stamp writes the tier and keeps the facing and every
/// other cell, a plain (or absent) map leaves the template as it was, and opening and breaking unopened roll the same
/// biome table.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CaveChestTests
{
    public CaveChestTests() => TestContent.EnsureLoaded();

    private static byte Chest => ContentRegistry.RequireBlock("ruin_chest_underground").Id;
    private static ChestProfile Profile => BlockType.ById[Chest]!.Chest!;

    private static string Describe(ChestState c) => string.Join(",", Enumerable.Range(0, c.SlotCount)
        .Select(i => c[i]).Where(s => !s.IsEmpty).Select(s => $"{s.Id}x{s.Count}:{s.Instance?.ResolvedName}"));

    private static CaveBiomeMap MapOf(CaveBiome biome)
    {
        var map = new CaveBiomeMap();
        Array.Fill(map.Cells, (byte)biome);
        map.Recount();
        return map;
    }

    // Every cell the whole footprint writes (block | state << 8), stamped through the chunks it spans from origin (0, 0, 0)
    private static Dictionary<(int, int, int), int> StampAll(StructureTemplate template, int turns, CaveBiomeMap? map)
    {
        var written = new Dictionary<(int, int, int), int>();
        foreach (var cx in new[] { 0, Chunk.Size })
        foreach (var cy in new[] { 0, Chunk.Size })
        foreach (var cz in new[] { 0, Chunk.Size })
        {
            var blocks = new byte[Chunk.BlockCount];
            byte[]? state = null;
            StructureStamper.Stamp(blocks, ref state, cx, cy, cz, template, turns, 0, 0, 0, int.MinValue, int.MaxValue,
                chestBiomes: map);
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var ly = 0; ly < Chunk.Size; ly++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                var i = Chunk.Idx(lx, ly, lz);
                var s = state?[i] ?? 0;
                if (blocks[i] == 0 && s == 0) continue;
                written[(cx + lx, cy + ly, cz + lz)] = blocks[i] | (s << 8);
            }
        }
        return written;
    }

    private static IEnumerable<StructureTemplate> ChestRooms =>
        StructureRegistry.Of(StructureKind.Underground).Where(t => StampAll(t, 0, null).Values.Any(c => (c & 0xFF) == Chest));

    [Fact]
    public void EveryCaveBiomeHasItsOwnLoadedChestTable()
    {
        var names = new List<string>();
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            var name = Profile.TableFor(ChestProfile.TierState((int)biome));
            Assert.NotNull(name);
            Assert.NotNull(ContentRegistry.LootTableFor(name!));
            names.Add(name!);
        }
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void AStampedUndergroundChestCarriesItsCellsCaveBiomeAndKeepsItsFacing()
    {
        var rooms = ChestRooms.ToList();
        Assert.NotEmpty(rooms);
        var fungal = MapOf(CaveBiome.Fungal);
        foreach (var template in rooms)
        for (var turns = 0; turns < 4; turns++)
        {
            var plain = StampAll(template, turns, null);
            var flavoured = StampAll(template, turns, fungal);
            Assert.Equal(plain.Keys.OrderBy(k => k), flavoured.Keys.OrderBy(k => k));

            var chests = 0;
            foreach (var (cell, before) in plain)
            {
                var after = flavoured[cell];
                Assert.Equal(before & 0xFF, after & 0xFF);
                if ((before & 0xFF) != Chest)
                {
                    Assert.Equal(before, after);
                    continue;
                }
                chests++;
                var stateBefore = (byte)(before >> 8);
                var stateAfter = (byte)(after >> 8);
                Assert.Equal(0, ChestProfile.TierOf(stateBefore));
                Assert.Equal((int)CaveBiome.Fungal, ChestProfile.TierOf(stateAfter));
                Assert.Equal(stateBefore & 3, stateAfter & 3);
            }
            Assert.True(chests > 0, $"{template.Name} stamped no chest at turn {turns}");
        }
    }

    [Fact]
    public void APlainMapLeavesTheChestUntouched()
    {
        var plainMap = MapOf(CaveBiome.Plain);
        foreach (var template in ChestRooms)
        for (var turns = 0; turns < 4; turns++)
            Assert.Equal(StampAll(template, turns, null), StampAll(template, turns, plainMap));
    }

    [Fact]
    public void OpeningAndBreakingUnopenedRollTheSameBiomeTable()
    {
        var crystal = (byte)(ChestProfile.TierState((int)CaveBiome.Crystal) | 1);
        var plain = (byte)(ChestProfile.TierState((int)CaveBiome.Plain) | 1);

        Assert.Equal(Describe(ChestLoot.Materialize(Profile, crystal, 3, -80, 7, 4)),
            Describe(ChestLoot.Materialize(Profile, crystal, 3, -80, 7, 4)));
        Assert.Contains(Enumerable.Range(0, 8), x =>
            Describe(ChestLoot.Materialize(Profile, crystal, x, -80, 7, 4))
            != Describe(ChestLoot.Materialize(Profile, plain, x, -80, 7, 4)));

        var (manager, center) = MeshTestWorld.WalledArea(Vector3.Zero);
        center.SetBlockRaw(4, 4, 4, Chest, crystal);
        var chest = new ChestManager().Open(manager, 4, 4, 4)!;
        var shown = Enumerable.Range(0, chest.SlotCount).Select(i => chest[i]).Where(s => !s.IsEmpty)
            .SelectMany(s => Enumerable.Repeat(s.Id, s.Count)).OrderBy(i => i).ToList();
        Assert.NotEmpty(shown);

        var drops = new DropManager();
        new ChestManager().NotifyBlockRemoved(4, 4, 4, Profile, crystal, drops);
        Assert.Equal(shown, drops.Drops.Select(d => d.BlockId).OrderBy(i => i).ToList());
    }
}
