using System;
using System.IO;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The GL-free rules a server judges a client's world intents by: the wire form round-trips and
/// refuses malformed fields, a break completes only once the held item's hits at its pace add up
/// to the block's hardness (and never with a refused tool, a changed target or a changed item),
/// the edit rate bucket refuses a flood, and a placement may only go against a face the eye sees.
/// </summary>
public sealed class WorldIntentRuleTests
{
    public WorldIntentRuleTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 Eye = new(0.5f, 65.62f, 0.5f);

    private static byte BlockOf(string name) => ContentRegistry.RequireBlock(name).Id;

    private static WorldIntent RoundTrip(int seq, WorldIntent intent, out int readSeq)
    {
        using var writer = new PacketWriter(NetMessage.WorldIntent);
        WorldIntentCodec.Write(writer, seq, intent);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.WorldIntent, reader.Message);
        return WorldIntentCodec.Read(reader, out readSeq);
    }

    [Fact]
    public void EveryIntentKindRoundTripsTheFieldsItCarries()
    {
        var direction = Vector3.Normalize(new Vector3(1f, -0.25f, 0.5f));
        var cell = new Vector3(3f, 64f, -2f);
        var intents = new[]
        {
            WorldIntent.BreakStart(Eye, direction, cell),
            WorldIntent.BreakComplete(Eye, direction, cell),
            WorldIntent.Place(Eye, direction, cell, -Vector3.UnitX),
            WorldIntent.Use(Eye, direction, cell),
            WorldIntent.Use(Eye, direction, null),
            WorldIntent.Throw(Eye, direction, 0.75f),
            WorldIntent.AttackWith(Eye, direction, NetAttackKind.SliceReturn),
        };

        var seq = 40;
        foreach (var intent in intents)
        {
            var back = RoundTrip(++seq, intent, out var readSeq);
            Assert.Equal(seq, readSeq);
            Assert.Equal(intent.Kind, back.Kind);
            Assert.Equal(intent.Eye, back.Eye);
            Assert.True(Vector3.Distance(intent.Direction, back.Direction) < 1e-5f);
            Assert.Equal(intent.HasCell, back.HasCell);
            if (intent.HasCell)
                Assert.Equal(intent.Cell, back.Cell);
            Assert.Equal(intent.Normal, back.Normal);
            Assert.Equal(intent.Charge, back.Charge);
            Assert.Equal(intent.Attack, back.Attack);
        }
    }

    [Fact]
    public void TheAimIsRenormalisedAndTheChargeClamped()
    {
        var back = RoundTrip(1, WorldIntent.Throw(Eye, new Vector3(0f, 0f, -1.3f), 7f), out _);
        Assert.Equal(1f, back.Direction.Length(), precision: 5);
        Assert.Equal(1f, back.Charge);
    }

    [Fact]
    public void MalformedIntentsAreRefusedAsBadFrames()
    {
        static byte[] Frame(Action<PacketWriter> body)
        {
            using var writer = new PacketWriter(NetMessage.WorldIntent);
            body(writer);
            return writer.ToArray();
        }

        static void AssertBad(byte[] frame)
        {
            using var reader = new PacketReader(frame);
            _ = reader.Message;
            Assert.ThrowsAny<Exception>(() => WorldIntentCodec.Read(reader, out _));
        }

        // An unknown kind
        AssertBad(Frame(w => { w.Write(1); w.Write((byte)99); w.Write(Eye); w.Write(Vector3.UnitX); }));
        // A non-finite eye
        AssertBad(Frame(w => { w.Write(1); w.Write((byte)NetIntentKind.Throw); w.Write(new Vector3(float.NaN, 0f, 0f)); w.Write(Vector3.UnitX); w.Write(0.5f); }));
        // An aim that is no direction at all
        AssertBad(Frame(w => { w.Write(1); w.Write((byte)NetIntentKind.Throw); w.Write(Eye); w.Write(Vector3.Zero); w.Write(0.5f); }));
        // A face normal component outside -1..1
        AssertBad(Frame(w =>
        {
            w.Write(1); w.Write((byte)NetIntentKind.Place); w.Write(Eye); w.Write(Vector3.UnitX);
            w.Write(1); w.Write(2); w.Write(3); w.Write((byte)2); w.Write((byte)0); w.Write((byte)0);
        }));
        // A short frame
        AssertBad(Frame(w => { w.Write(1); w.Write((byte)NetIntentKind.BreakStart); w.Write(Eye); }));

        using var reader = new PacketReader(Frame(w => { w.Write(1); w.Write((byte)99); w.Write(Eye); w.Write(Vector3.UnitX); }));
        _ = reader.Message;
        Assert.Throws<InvalidDataException>(() => WorldIntentCodec.Read(reader, out _));
    }

    [Fact]
    public void ABreakCompletesOnlyOnceItsHitsAtTheHeldPaceReachTheHardness()
    {
        var dirt = BlockOf("dirt");
        var required = BreakProgress.RequiredSeconds(Mining.HardnessOf(dirt, 0), Mining.BlockDamage(null),
            Mining.HitGapSeconds(null));
        Assert.True(required > 0.0, "dirt should take more than one bare-hand hit");

        var progress = new BreakProgress();
        progress.Start(1, 2, 3, dirt, 0, null, 0, 0, now: 100.0);
        Assert.Equal(BreakVerdict.TooFast, progress.Complete(1, 2, 3, dirt, 0, 0, false, 100.0 + required * 0.5, 0.0));

        // The verdict ended that break: a completion with no start is refused...
        Assert.Equal(BreakVerdict.NotStarted, progress.Complete(1, 2, 3, dirt, 0, 0, false, 200.0, 0.0));

        // ...and one at the full pace (or within the slack of it) breaks the block
        progress.Start(1, 2, 3, dirt, 0, null, 0, 0, now: 300.0);
        Assert.Equal(BreakVerdict.Break, progress.Complete(1, 2, 3, dirt, 0, 0, false, 300.0 + required, 0.0));
        progress.Start(1, 2, 3, dirt, 0, null, 0, 0, now: 400.0);
        Assert.Equal(BreakVerdict.Break, progress.Complete(1, 2, 3, dirt, 0, 0, false, 400.0 + required - 0.05, 0.1));
    }

    [Fact]
    public void ARefusedToolNeverBreaksTheBlockHoweverLongItMines()
    {
        var stone = BlockOf("stone");
        Assert.False(Mining.CanMine(stone, null));

        var progress = new BreakProgress();
        progress.Start(0, 0, 0, stone, 0, null, 0, 0, now: 0.0);
        Assert.Equal(BreakVerdict.ToolRefused, progress.Complete(0, 0, 0, stone, 0, 0, false, 1e6, 0.0));
    }

    [Fact]
    public void AChangedTargetOrHeldItemRestartsTheBreak()
    {
        var dirt = BlockOf("dirt");
        var stone = BlockOf("stone");
        var progress = new BreakProgress();

        progress.Start(1, 2, 3, dirt, 0, null, 0, 0, now: 0.0);
        Assert.Equal(BreakVerdict.OtherTarget, progress.Complete(1, 2, 4, dirt, 0, 0, false, 1e6, 0.0));

        // The same cell no longer holding the block mined is another target too
        progress.Start(1, 2, 3, dirt, 0, null, 0, 0, now: 0.0);
        Assert.Equal(BreakVerdict.OtherTarget, progress.Complete(1, 2, 3, stone, 0, 0, false, 1e6, 0.0));

        // A different item (or another instance of a tool) in hand since the start
        var flint = ContentRegistry.RequireItem("flint");
        progress.Start(1, 2, 3, dirt, 0, flint, flint.Id, 0, now: 0.0);
        Assert.Equal(BreakVerdict.HeldChanged, progress.Complete(1, 2, 3, dirt, 0, 0, false, 1e6, 0.0));
        progress.Start(1, 2, 3, dirt, 0, null, 7, 1, now: 0.0);
        Assert.Equal(BreakVerdict.HeldChanged, progress.Complete(1, 2, 3, dirt, 7, 2, false, 1e6, 0.0));
    }

    [Fact]
    public void ACreativePlayerBreaksAtOnceWithOrWithoutAStart()
    {
        var stone = BlockOf("stone");
        var progress = new BreakProgress();
        Assert.Equal(BreakVerdict.Break, progress.Complete(0, 0, 0, stone, 0, 0, creative: true, 0.0, 0.0));
        progress.Start(0, 0, 0, stone, 0, null, 0, 0, now: 0.0);
        Assert.Equal(BreakVerdict.Break, progress.Complete(0, 0, 0, stone, 0, 0, creative: true, 0.0, 0.0));
    }

    [Fact]
    public void TheRequiredTimeFollowsHardnessDamageAndPace()
    {
        // A block of no hardness pops on the first hit; no damage never gets there
        Assert.Equal(0.0, BreakProgress.RequiredSeconds(0f, 0.5f, 0.3f));
        Assert.True(double.IsPositiveInfinity(BreakProgress.RequiredSeconds(5f, 0f, 0.3f)));

        // Harder takes no less, more damage per hit takes no more, a slower pace takes no less
        for (var hardness = 0.5f; hardness <= 24f; hardness += 0.5f)
        {
            Assert.True(BreakProgress.RequiredSeconds(hardness + 1f, 0.5f, 0.3f)
                >= BreakProgress.RequiredSeconds(hardness, 0.5f, 0.3f));
            Assert.True(BreakProgress.RequiredSeconds(hardness, 1.5f, 0.3f)
                <= BreakProgress.RequiredSeconds(hardness, 0.5f, 0.3f));
            Assert.True(BreakProgress.RequiredSeconds(hardness, 0.5f, 0.4f)
                >= BreakProgress.RequiredSeconds(hardness, 0.5f, 0.3f));
        }
    }

    [Fact]
    public void TheEditRateRefusesAFloodAndRefillsOverTime()
    {
        var limiter = new RateLimiter(perSecond: 10f, burst: 5f);
        var taken = 0;
        for (var i = 0; i < 20; i++)
            if (limiter.TryTake(0.0))
                taken++;
        // A burst passes, the rest of the flood at the same instant does not
        Assert.Equal(5, taken);
        Assert.False(limiter.TryTake(0.0));

        // A steady stream at the rate keeps passing
        for (var i = 1; i <= 50; i++)
            Assert.True(limiter.TryTake(i * 0.1));
    }

    [Fact]
    public void APlacementMayOnlyGoAgainstAFaceTheEyeSees()
    {
        // A wall cell two blocks along +X from the eye
        var cell = new Vector3(2f, 65f, 0f);
        Assert.True(NetValidation.FaceFacesEye(cell, -Vector3.UnitX, Eye));
        // The far face would place behind the wall
        Assert.False(NetValidation.FaceFacesEye(cell, Vector3.UnitX, Eye));
        // A diagonal or a scaled normal is no face at all
        Assert.False(NetValidation.FaceFacesEye(cell, new Vector3(-1f, 1f, 0f), Eye));
        Assert.False(NetValidation.FaceFacesEye(cell, new Vector3(-2f, 0f, 0f), Eye));
    }

    [Fact]
    public void AnIntentsEyeMustSitNearThePlayer()
    {
        var drift = WorldGenerationConfig.NetMaxIntentEyeDriftBlocks;
        Assert.True(NetValidation.EyeWithin(Eye, Eye + new Vector3(drift * 0.5f, 0f, 0f), drift));
        Assert.False(NetValidation.EyeWithin(Eye, Eye + new Vector3(drift * 2f, 0f, 0f), drift));
    }
}
