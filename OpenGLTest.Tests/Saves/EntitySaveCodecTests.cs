using System.Linq;
using System.Text;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="EntitySaveCodec"/>: mobs and drops survive a write and a read, a missing list
/// reads as null, and bad input reads as "no file" instead of throwing. Content-free: the codec
/// never resolves a name.
/// </summary>
public sealed class EntitySaveCodecTests
{
    [Fact]
    public void RoundTripsMobsAndDrops()
    {
        var original = new EntitySaveData
        {
            Mobs =
            [
                new EntitySaveData.MobData
                {
                    Type = "a", Position = [1.5f, 70.25f, -3f], Health = 7, Facing = 1.25f,
                    Velocity = [0.5f, -2f, 0.125f],
                },
                new EntitySaveData.MobData
                {
                    Type = "b", Position = [-8f, 64.65f, 12.5f], Health = 3, Facing = -2.5f,
                    Velocity = [0f, 0f, 0f], Ammo = 4,
                },
            ],
            Drops =
            [
                new EntitySaveData.DropData
                {
                    Item = "block/c", Position = [2.5f, 65.1f, 2.5f], Velocity = [0.75f, 3f, -0.5f], Age = 0.2f,
                },
                new EntitySaveData.DropData
                {
                    Item = "item/d", Position = [-1f, 66f, 4f], Velocity = [0f, 0f, 0f], Age = 12.5f,
                },
            ],
        };

        var back = EntitySaveCodec.TryParse(EntitySaveCodec.ToBytes(original));

        Assert.NotNull(back);
        Assert.NotNull(back.Mobs);
        Assert.NotNull(back.Drops);
        Assert.Equal(original.Mobs.Count, back.Mobs.Count);
        for (var i = 0; i < original.Mobs.Count; i++)
        {
            var a = original.Mobs[i];
            var b = back.Mobs[i];
            Assert.Equal(a.Type, b.Type);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Health, b.Health);
            Assert.Equal(a.Facing, b.Facing);
            Assert.Equal(a.Velocity, b.Velocity);
            Assert.Equal(a.Ammo, b.Ammo);
        }

        Assert.Equal(original.Drops.Count, back.Drops.Count);
        for (var i = 0; i < original.Drops.Count; i++)
        {
            var a = original.Drops[i];
            var b = back.Drops[i];
            Assert.Equal(a.Item, b.Item);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Velocity, b.Velocity);
            Assert.Equal(a.Age, b.Age);
        }
    }

    [Fact]
    public void MissingListsReadAsNullAndBadInputAsNoFile()
    {
        var empty = EntitySaveCodec.TryParse("{}"u8.ToArray());
        Assert.NotNull(empty);
        Assert.Null(empty.Mobs);
        Assert.Null(empty.Drops);

        var valid = EntitySaveCodec.ToBytes(new EntitySaveData
        {
            Mobs = [new EntitySaveData.MobData { Type = "a", Position = [1f, 2f, 3f], Health = 5 }],
            Drops = [new EntitySaveData.DropData { Item = "block/c", Position = [1f, 2f, 3f] }],
        });

        Assert.Null(EntitySaveCodec.TryParse(null));
        Assert.Null(EntitySaveCodec.TryParse([]));
        Assert.Null(EntitySaveCodec.TryParse([0xFF, 0xFE, 0x00, 0x13, 0x80]));
        Assert.Null(EntitySaveCodec.TryParse(Encoding.UTF8.GetBytes("not json at all")));
        Assert.Null(EntitySaveCodec.TryParse(valid.Take(valid.Length / 2).ToArray()));
        Assert.Null(EntitySaveCodec.TryParse("null"u8.ToArray()));
    }
}
