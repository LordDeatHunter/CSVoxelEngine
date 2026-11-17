using System;
using System.Text;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Ingots;
using VoxelEngine.Simulation.Lanterns;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.Simulation.Stumps;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>Null station rows are skipped independently so a valid sibling can still restore.</summary>
public sealed class StationNullRecordTests
{
    public StationNullRecordTests() => TestContent.EnsureLoaded();

    private static StationSaveData Parse(byte[] utf8)
    {
        var data = StationSaveCodec.TryParse(utf8);
        Assert.NotNull(data);
        return data!;
    }

    private static byte[] WithNullFirst(string property, StationSaveData data)
    {
        var json = Encoding.UTF8.GetString(StationSaveCodec.ToBytes(data));
        var marker = $"\"{property}\": [";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Serialized {property} list was missing");
        return Encoding.UTF8.GetBytes(json.Insert(index + marker.Length, "null,"));
    }

    private static StationSaveData.CampfireData Campfire() => new()
    {
        Cell = [1, 2, 3], Lit = true, BurnLeft = 12f,
        Slots = [new StationSaveData.CookSlotData
        {
            Input = "item/mudball", Output = "item/mud_brick", Count = 1,
            TimeLeft = 8f, TotalTime = 20f,
        }],
    };

    [Fact]
    public void NullCampfireDoesNotHideValidSibling()
    {
        var saved = new StationSaveData { Campfires = [Campfire()] };
        var control = new CampfireManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Campfires));
        Assert.True(control.IsLit(1, 2, 3));

        var restored = new CampfireManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Campfires", saved)).Campfires));
        Assert.True(restored.IsLit(1, 2, 3));
        Assert.Single(restored.Capture());
    }

    [Fact]
    public void NullCampfireSlotDoesNotHideValidSibling()
    {
        var saved = new StationSaveData { Campfires = [Campfire()] };
        var json = Encoding.UTF8.GetString(StationSaveCodec.ToBytes(saved));
        var marker = "\"Slots\": [";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var bytes = Encoding.UTF8.GetBytes(json.Insert(index + marker.Length, "null,"));

        var control = new CampfireManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Campfires));
        Assert.Single(control.CookingAt(1, 2, 3)!);

        var restored = new CampfireManager();
        Assert.Equal(1, restored.Restore(Parse(bytes).Campfires));
        var slot = Assert.Single(restored.CookingAt(1, 2, 3)!);
        Assert.Equal("item/mudball", SaveNames.NameOf(slot.InputId));
        Assert.Equal("item/mud_brick", SaveNames.NameOf(slot.OutputId));
    }

    [Fact]
    public void NullQuernDoesNotHideValidSibling()
    {
        var saved = new StationSaveData
        {
            Querns = [new StationSaveData.QuernData { Cell = [5, 2, 3], Input = "item/malachite_ore", Angle = 1.25f, Spinning = true }],
        };
        var control = new QuernManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Querns));
        Assert.Equal("item/malachite_ore", control.Capture()[0].Input);

        var restored = new QuernManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Querns", saved)).Querns));
        var row = Assert.Single(restored.Capture());
        Assert.Equal("item/malachite_ore", row.Input);
        Assert.Equal(1.25f, row.Angle);
        Assert.True(row.Spinning);
    }

    [Fact]
    public void NullKilnDoesNotHideValidSibling()
    {
        var saved = new StationSaveData
        {
            Kilns = [new StationSaveData.KilnData
            {
                Cell = [8, 2, 3], Anchor = "block/mud_brick_kiln", Facing = 2, Fuel = 1,
                FuelItem = "item/coal", FuelSeconds = 0f, Lit = false, BurnLeft = 0f, MeltLeft = 0f,
            }],
        };
        var control = new KilnManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Kilns));
        Assert.Equal(1, Assert.Single(control.Capture()).Fuel);

        var restored = new KilnManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Kilns", saved)).Kilns));
        var row = Assert.Single(restored.Capture());
        Assert.Equal("block/mud_brick_kiln", row.Anchor);
        Assert.Equal(1, row.Fuel);
    }

    [Fact]
    public void NullCastDoesNotHideValidSibling()
    {
        var mold = SaveNames.NameOf(ContentRegistry.RequireBlock("fired_mold_pickaxe").Id)!;
        var material = ContentRegistry.MoltenMaterialByName("copper")!.Name;
        var saved = new StationSaveData
        {
            Casts = [new StationSaveData.CastData { Cell = [12, 2, 3], Mold = mold, Material = material, FilledMl = 40, Cooling = true, CoolLeft = 2f }],
        };
        var control = new CastingManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Casts));
        Assert.Equal(40, Assert.Single(control.Capture()).FilledMl);

        var restored = new CastingManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Casts", saved)).Casts));
        var row = Assert.Single(restored.Capture());
        Assert.Equal(mold, row.Mold);
        Assert.Equal(40, row.FilledMl);
    }

    [Fact]
    public void NullBloomeryDoesNotHideValidSibling()
    {
        var saved = new StationSaveData
        {
            Bloomeries = [new StationSaveData.BloomeryData
            {
                Cell = [2, 2, 2], Anchor = "block/bloomery", Facing = 0, Ore = 1, Fuel = 0,
                FuelSeconds = 0f, Lit = false, BurnLeft = 0f, Heat = 0f, UnitProgress = 0f,
            }],
        };
        var control = new BloomeryManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Bloomeries));
        Assert.Equal(1, Assert.Single(control.Capture()).Ore);

        var restored = new BloomeryManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Bloomeries", saved)).Bloomeries));
        Assert.Equal(1, Assert.Single(restored.Capture()).Ore);
    }

    [Fact]
    public void NullAnvilDoesNotHideValidSibling()
    {
        var bloom = ContentRegistry.RequireBlock("bloomery").Bloomery!;
        var metal = SaveNames.NameOf(bloom.BloomId)!;
        var saved = new StationSaveData
        {
            Anvils = [new StationSaveData.AnvilData { Cell = [4, 2, 4], Block = "block/anvil", Metal = metal, Count = 1, Cooldown = 0f }],
        };
        var control = new AnvilManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Anvils));
        Assert.Equal(1, Assert.Single(control.Capture()).Count);

        var restored = new AnvilManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Anvils", saved)).Anvils));
        var row = Assert.Single(restored.Capture());
        Assert.Equal(metal, row.Metal);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public void NullStumpDoesNotHideValidSibling()
    {
        var saved = new StationSaveData
        {
            Stumps = [new StationSaveData.StumpData { Cell = [4, 1, 4], Log = "block/oak_log", OffsetX = 0.2f, OffsetZ = -0.2f, Yaw = 0.4f, Hits = 1 }],
        };
        var control = new StumpManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Stumps));
        Assert.Equal("block/oak_log", Assert.Single(control.Capture()).Log);

        var restored = new StumpManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Stumps", saved)).Stumps));
        var row = Assert.Single(restored.Capture());
        Assert.Equal("block/oak_log", row.Log);
        Assert.Equal(0.2f, row.OffsetX);
    }

    [Fact]
    public void NullIngotPileDoesNotHideValidSibling()
    {
        var saved = new StationSaveData
        {
            IngotPiles = [new StationSaveData.IngotPileData { Cell = [2, 3, 4], Ingots = ["item/copper_ingot", "item/tin_ingot"] }],
        };
        var control = new IngotPileManager();
        Assert.Equal(0, control.Restore(Parse(StationSaveCodec.ToBytes(saved)).IngotPiles));
        Assert.Equal(new[] { "item/copper_ingot", "item/tin_ingot" }, Assert.Single(control.Capture()).Ingots);

        var restored = new IngotPileManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("IngotPiles", saved)).IngotPiles));
        var row = Assert.Single(restored.Capture());
        Assert.Equal(new[] { "item/copper_ingot", "item/tin_ingot" }, row.Ingots);
    }

    [Fact]
    public void NullLanternDoesNotHideValidSibling()
    {
        var saved = new StationSaveData
        {
            Lanterns = [new LanternData { Cell = [1, 2, 3], Block = "block/lantern", Remaining = 10f, Capacity = 60f }],
        };
        var control = new LanternManager();
        control.Restore(Parse(StationSaveCodec.ToBytes(saved)).Lanterns);
        Assert.Equal(10f, Assert.Single(control.Capture()).Remaining);

        var restored = new LanternManager();
        Assert.Equal(1, restored.Restore(Parse(WithNullFirst("Lanterns", saved)).Lanterns));
        Assert.Equal(10f, Assert.Single(restored.Capture()).Remaining);
    }

    [Fact]
    public void LanternRestoreKeepsStrictNestedStorageValidationAndCountsOnlyNullRows()
    {
        Assert.Equal(0, new LanternManager().Restore(null));
        var invalid = new LanternData
        {
            Cell = [1, 2, 3], Block = "block/lantern", Remaining = 10f, Capacity = 60f,
            Storage = new StationSaveData.ChestData { SlotCount = LanternConfig.Slots, Slots = [null!] },
        };
        var parsed = Parse(StationSaveCodec.ToBytes(new StationSaveData { Lanterns = [invalid] }));

        Assert.Throws<System.IO.InvalidDataException>(() => new LanternManager().Restore(parsed.Lanterns));
    }
}
