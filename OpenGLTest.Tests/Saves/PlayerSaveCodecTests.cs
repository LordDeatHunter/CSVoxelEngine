using System.Collections.Generic;
using System.Linq;
using System.Text;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// <see cref="PlayerSaveCodec"/>: every field of <see cref="PlayerSaveData"/> survives a write
/// and a read, a missing field reads as null, and bad input reads as "no file" instead of
/// throwing. Content-free: the codec never resolves a name.
/// </summary>
public sealed class PlayerSaveCodecTests
{
    [Fact]
    public void RoundTripsEveryField()
    {
        var original = new PlayerSaveData
        {
            Position = [3.5f, 81.25f, -7.75f],
            Yaw = 33.5f,
            Pitch = -12.25f,
            Creative = true,
            Flying = false,
            Health = 42,
            LightingClock = 1234.5678,
            RespawnEye = [1f, 70.5f, -2f],
            Inventory = new PlayerSaveData.InventoryData
            {
                Blocks =
                [
                    new PlayerSaveData.StackData { Name = "block/a", Count = 12 },
                    new PlayerSaveData.StackData { Name = "block/b", Count = 3 },
                ],
                Misc = [new PlayerSaveData.StackData { Name = "item/c", Count = 7 }],
                Tools =
                [
                    new PlayerSaveData.ToolData { InstanceId = 4, Name = "item/d", UsesLeft = 17, MaxUses = 40 },
                    new PlayerSaveData.ToolData
                    {
                        InstanceId = 9,
                        Name = "item/e",
                        Crucible = new PlayerSaveData.CrucibleData
                        {
                            Material = "m", MoltenMl = 40, PendingMl = 60, PendingInput = "block/f",
                        },
                        CustomName = "Old Faithful",
                        Enchantments = new Dictionary<string, int> { ["sharp"] = 2, ["swift"] = 1 },
                    },
                ],
                Hotbar = Enumerable.Range(0, 10).Select(i => i % 3 == 0
                        ? new PlayerSaveData.HotbarSlotData { IsTool = true, ToolInstance = i + 1 }
                        : new PlayerSaveData.HotbarSlotData { IsTool = false, Name = i % 3 == 1 ? $"block/s{i}" : null, ToolInstance = 0 })
                    .ToList(),
                SelectedIndex = 4,
                NextToolInstance = 10,
            },
        };

        var back = PlayerSaveCodec.TryParse(PlayerSaveCodec.ToBytes(original));

        Assert.NotNull(back);
        Assert.Equal(original.Position, back.Position);
        Assert.Equal(original.Yaw, back.Yaw);
        Assert.Equal(original.Pitch, back.Pitch);
        Assert.Equal(original.Creative, back.Creative);
        Assert.Equal(original.Flying, back.Flying);
        Assert.Equal(original.Health, back.Health);
        Assert.Equal(original.LightingClock, back.LightingClock);
        Assert.Equal(original.RespawnEye, back.RespawnEye);

        var a = original.Inventory;
        var b = back.Inventory;
        Assert.NotNull(b);
        AssertStacksEqual(a.Blocks!, b.Blocks);
        AssertStacksEqual(a.Misc!, b.Misc);

        Assert.NotNull(b.Tools);
        Assert.Equal(a.Tools!.Count, b.Tools.Count);
        for (var i = 0; i < a.Tools.Count; i++)
        {
            var ta = a.Tools[i];
            var tb = b.Tools[i];
            Assert.Equal(ta.InstanceId, tb.InstanceId);
            Assert.Equal(ta.Name, tb.Name);
            Assert.Equal(ta.UsesLeft, tb.UsesLeft);
            Assert.Equal(ta.MaxUses, tb.MaxUses);
            Assert.Equal(ta.CustomName, tb.CustomName);
            Assert.Equal(ta.Enchantments, tb.Enchantments);
            if (ta.Crucible == null)
            {
                Assert.Null(tb.Crucible);
                continue;
            }

            Assert.NotNull(tb.Crucible);
            Assert.Equal(ta.Crucible.Material, tb.Crucible.Material);
            Assert.Equal(ta.Crucible.MoltenMl, tb.Crucible.MoltenMl);
            Assert.Equal(ta.Crucible.PendingMl, tb.Crucible.PendingMl);
            Assert.Equal(ta.Crucible.PendingInput, tb.Crucible.PendingInput);
        }

        Assert.NotNull(b.Hotbar);
        Assert.Equal(a.Hotbar!.Count, b.Hotbar.Count);
        for (var i = 0; i < a.Hotbar.Count; i++)
        {
            Assert.Equal(a.Hotbar[i].IsTool, b.Hotbar[i].IsTool);
            Assert.Equal(a.Hotbar[i].Name, b.Hotbar[i].Name);
            Assert.Equal(a.Hotbar[i].ToolInstance, b.Hotbar[i].ToolInstance);
        }

        Assert.Equal(a.SelectedIndex, b.SelectedIndex);
        Assert.Equal(a.NextToolInstance, b.NextToolInstance);
    }

    [Fact]
    public void MissingFieldsReadAsNullAndBadInputAsNoFile()
    {
        var empty = PlayerSaveCodec.TryParse("{}"u8.ToArray());
        Assert.NotNull(empty);
        Assert.Null(empty.Position);
        Assert.Null(empty.Yaw);
        Assert.Null(empty.Pitch);
        Assert.Null(empty.Creative);
        Assert.Null(empty.Flying);
        Assert.Null(empty.Health);
        Assert.Null(empty.LightingClock);
        Assert.Null(empty.RespawnEye);
        Assert.Null(empty.Inventory);

        var valid = PlayerSaveCodec.ToBytes(new PlayerSaveData
        {
            Position = [1f, 2f, 3f],
            Health = 5,
            Inventory = new PlayerSaveData.InventoryData { SelectedIndex = 2 },
        });

        Assert.Null(PlayerSaveCodec.TryParse(null));
        Assert.Null(PlayerSaveCodec.TryParse([]));
        Assert.Null(PlayerSaveCodec.TryParse([0xFF, 0xFE, 0x00, 0x7B, 0x13, 0x88]));
        Assert.Null(PlayerSaveCodec.TryParse(Encoding.UTF8.GetBytes("not json at all")));
        Assert.Null(PlayerSaveCodec.TryParse(valid[..(valid.Length / 2)]));
        Assert.Null(PlayerSaveCodec.TryParse("null"u8.ToArray()));
    }

    private static void AssertStacksEqual(List<PlayerSaveData.StackData> expected, List<PlayerSaveData.StackData>? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Select(s => (s.Name, s.Count)), actual.Select(s => (s.Name, s.Count)));
    }
}
