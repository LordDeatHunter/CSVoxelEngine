using System;
using System.IO;
using System.Linq;
using System.Text;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

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
                    PickupDelay = 2f,
                },
                new EntitySaveData.DropData
                {
                    Item = "item/d", Position = [-1f, 66f, 4f], Velocity = [0f, 0f, 0f], Age = 12.5f,
                    PickupDelay = -3.5f,
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
            Assert.Equal(a.PickupDelay, b.PickupDelay);
        }
    }

    [Theory]
    [InlineData("1e1000")]
    [InlineData("-1e1000")]
    public void NonFinitePickupDelayFallsBackWithoutRejectingOtherEntityData(string rawNumber)
    {
        var json = $$"""
            {"Drops":[
              {"Item":"block/stone","Age":0.75,"PickupDelay":{{rawNumber}}},
              {"Item":"item/torch","Age":1.25,"PickupDelay":-3.5},
              {"Item":"block/stone","Age":0.5,"PickupDelay":null},
              {"Item":"block/stone","Age":0.25}
            ]}
            """;

        var parsed = EntitySaveCodec.TryParse(Encoding.UTF8.GetBytes(json), out var rejectedJson);

        Assert.NotNull(parsed);
        Assert.False(rejectedJson);
        Assert.Collection(parsed!.Drops!,
            invalid => { Assert.Equal(0.75f, invalid.Age); Assert.Null(invalid.PickupDelay); },
            finite => { Assert.Equal(1.25f, finite.Age); Assert.Equal(-3.5f, finite.PickupDelay); },
            explicitNull => { Assert.Equal(0.5f, explicitNull.Age); Assert.Null(explicitNull.PickupDelay); },
            absent => { Assert.Equal(0.25f, absent.Age); Assert.Null(absent.PickupDelay); });
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("\"Infinity\"")]
    public void NamedNonFinitePickupDelayTokensKeepExistingJsonRejection(string token)
    {
        var json = $"{{\"Drops\":[{{\"Item\":\"block/stone\",\"PickupDelay\":{token}}}]}}";

        Assert.Null(EntitySaveCodec.TryParse(Encoding.UTF8.GetBytes(json), out var rejectedJson));
        Assert.True(rejectedJson);
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

    [Fact]
    public void TryParseReportsOnlyJsonRejection()
    {
        Assert.Null(EntitySaveCodec.TryParse(null, out var missingRejected));
        Assert.False(missingRejected);
        Assert.Null(EntitySaveCodec.TryParse("null"u8.ToArray(), out var nullRejected));
        Assert.False(nullRejected);
        Assert.NotNull(EntitySaveCodec.TryParse("{}"u8.ToArray(), out var partialRejected));
        Assert.False(partialRejected);
        Assert.Null(EntitySaveCodec.TryParse("{\"Mobs\":["u8.ToArray(), out var malformedRejected));
        Assert.True(malformedRejected);
    }

    [Fact]
    public void NullEntityRecordsRemainSuccessfulCodecValues()
    {
        var absent = EntitySaveCodec.TryParse("{}"u8.ToArray(), out var absentRejected);
        Assert.NotNull(absent);
        Assert.False(absentRejected);
        Assert.Null(absent.Mobs);
        Assert.Null(absent.Drops);

        var nullLists = EntitySaveCodec.TryParse("{\"Mobs\":null,\"Drops\":null}"u8.ToArray(), out var nullListsRejected);
        Assert.NotNull(nullLists);
        Assert.False(nullListsRejected);
        Assert.Null(nullLists.Mobs);
        Assert.Null(nullLists.Drops);

        var emptyLists = EntitySaveCodec.TryParse("{\"Mobs\":[],\"Drops\":[]}"u8.ToArray(), out var emptyListsRejected);
        Assert.NotNull(emptyLists);
        Assert.False(emptyListsRejected);
        Assert.Empty(emptyLists.Mobs!);
        Assert.Empty(emptyLists.Drops!);

        var nullRecords = EntitySaveCodec.TryParse("{\"Mobs\":[null],\"Drops\":[null]}"u8.ToArray(), out var nullRecordsRejected);
        Assert.NotNull(nullRecords);
        Assert.False(nullRecordsRejected);
        Assert.Null(Assert.Single(nullRecords.Mobs!));
        Assert.Null(Assert.Single(nullRecords.Drops!));

        var emptyRecords = EntitySaveCodec.TryParse("{\"Mobs\":[{}],\"Drops\":[{}]}"u8.ToArray(), out var emptyRecordsRejected);
        Assert.NotNull(emptyRecords);
        Assert.False(emptyRecordsRejected);
        Assert.Null(Assert.Single(emptyRecords.Mobs!).Type);
        Assert.Null(Assert.Single(emptyRecords.Drops!).Item);

        var unresolved = EntitySaveCodec.TryParse("{\"Mobs\":[{\"Type\":\"retired-mob\"}],\"Drops\":[{\"Item\":\"item/retired\"}]}"u8.ToArray(), out var unresolvedRejected);
        Assert.NotNull(unresolved);
        Assert.False(unresolvedRejected);
        Assert.Equal("retired-mob", Assert.Single(unresolved.Mobs!).Type);
        Assert.Equal("item/retired", Assert.Single(unresolved.Drops!).Item);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("null", false)]
    [InlineData("{}", true)]
    [InlineData("{\"Mobs\":[{\"Type\":\"retired-mob\"}],\"Drops\":[{\"Item\":\"item/retired\"}]}", true)]
    [InlineData("{\"Drops\":[{\"Item\":\"block/stone\",\"PickupDelay\":1e1000}]}", true)]
    public void MissingNullPartialAndUnresolvedEntityFilesAreNotArchived(string? json, bool hasData)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, EntitySaveCodec.FileName);
        if (json != null)
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));

        var store = new ChunkSaveStore(directory);
        try
        {
            var loaded = EntitySaveReader.Read(store);
            Assert.Equal(hasData, loaded != null);
            store.EnqueueFile(EntitySaveCodec.FileName,
                EntitySaveCodec.ToBytes(new EntitySaveData { Mobs = [], Drops = [] }));
            Assert.True(store.Shutdown(TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(path + ".bad"));
            Assert.NotNull(EntitySaveCodec.TryParse(File.ReadAllBytes(path)));
        }
        finally
        {
            store.Shutdown(TimeSpan.FromSeconds(10));
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
