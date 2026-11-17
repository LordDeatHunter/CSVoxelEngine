using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Stations;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Leatherwork;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Leatherwork;

/// <summary>
/// The tanning vat's rules: water fills it once and hands back the emptied bucket, tannin goes in up to the capacity, a
/// batch of inputs goes in only while nothing tans or waits; the batch tans only with water and a tannin unit for every
/// input, pauses (keeping its progress) without them, and turns every input into its result while spending the tannin
/// and the water; an empty hand collects the result or takes back a batch that has not started. A gone block drops what
/// the vat held, and a vat survives a save and the wire.
/// </summary>
public sealed class TanningVatTests
{
    public TanningVatTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 Cell = new(4, 2, 4);

    private static BlockType VatBlock => BlockType.Types.Values.First(b => b.Tanning != null);

    private static TanningProfile Profile => VatBlock.Tanning!;

    private static ushort Bucket => ItemRegistry.TryGet(Profile.WaterId)!.UseResultId;

    private static TanningVatManager Ready()
    {
        var vats = new TanningVatManager();
        vats.Ensure(Cell, VatBlock.Id);
        return vats;
    }

    private static TanningVatManager.VatState State(TanningVatManager vats) => vats.StateAt(4, 2, 4)!;

    private static bool Use(TanningVatManager vats, ushort held, out VatUse use) =>
        vats.TryUse(Cell, held, held == Profile.WaterId ? Bucket : (ushort)0, out use);

    // A vat with water, tannin for every input and the batch of hides
    private static TanningVatManager Loaded(int hides)
    {
        var vats = Ready();
        Assert.True(Use(vats, Profile.WaterId, out _));
        for (var i = 0; i < hides; i++)
        {
            Assert.True(Use(vats, Profile.TanninId, out _));
            Assert.True(Use(vats, Profile.InputId, out _));
        }
        return vats;
    }

    [Fact]
    public void WaterFillsADryVatOnceAndHandsBackTheEmptiedBucket()
    {
        var vats = Ready();
        Assert.True(Use(vats, Profile.WaterId, out var use));
        Assert.Equal(new VatUse(true, Bucket, 1), use);
        Assert.True(State(vats).Water);
        Assert.False(Use(vats, Profile.WaterId, out _));
    }

    [Fact]
    public void TanninAndInputsGoInUpToTheCapacity()
    {
        var vats = Ready();
        for (var i = 0; i < Profile.Capacity; i++)
        {
            Assert.True(Use(vats, Profile.TanninId, out var tannin));
            Assert.True(tannin.Consume);
            Assert.True(Use(vats, Profile.InputId, out var input));
            Assert.True(input.Consume);
        }
        Assert.False(Use(vats, Profile.TanninId, out _));
        Assert.False(Use(vats, Profile.InputId, out _));
        Assert.Equal((Profile.Capacity, Profile.Capacity), (State(vats).Bark, State(vats).Hides));
    }

    [Fact]
    public void ABatchPausesWithoutWaterOrTanninEnoughAndKeepsItsProgress()
    {
        var vats = Ready();
        var state = State(vats);
        // Two inputs, one tannin unit, no water: nothing tans
        Assert.True(Use(vats, Profile.InputId, out _));
        Assert.True(Use(vats, Profile.InputId, out _));
        Assert.True(Use(vats, Profile.TanninId, out _));
        Assert.False(TanningRules.Step(state, Profile, Profile.Seconds * 2f));
        Assert.Equal(0f, state.Progress);

        // Water but still a tannin unit short: paused
        Assert.True(Use(vats, Profile.WaterId, out _));
        Assert.False(TanningRules.CanTan(state));
        Assert.True(Use(vats, Profile.TanninId, out _));
        Assert.True(TanningRules.CanTan(state));

        TanningRules.Step(state, Profile, Profile.Seconds * 0.5f);
        var half = state.Progress;
        Assert.True(half > 0f);
        // Losing the water pauses it without losing the progress
        state.Water = false;
        Assert.False(TanningRules.Step(state, Profile, Profile.Seconds));
        Assert.Equal(half, state.Progress);
    }

    [Fact]
    public void AFinishedBatchTurnsEveryInputIntoItsResultAndSpendsItsTanninAndWater()
    {
        var vats = Loaded(Profile.Capacity);
        var state = State(vats);
        // One spare tannin unit that the batch must not spend
        state.Bark++;

        Assert.False(TanningRules.Step(state, Profile, Profile.Seconds * 0.99f));
        Assert.True(TanningRules.Step(state, Profile, Profile.Seconds * 0.02f));
        Assert.Equal(Profile.Capacity * Profile.ResultPerInput, state.Leather);
        Assert.Equal((0, 1, false, 0f), (state.Hides, state.Bark, state.Water, state.Progress));
    }

    [Fact]
    public void NoInputJoinsABatchThatHasStartedOrAResultThatWaits()
    {
        var vats = Loaded(1);
        var state = State(vats);
        TanningRules.Step(state, Profile, Profile.Seconds * 0.5f);
        Assert.False(Use(vats, Profile.InputId, out _));
        // ...and a started batch cannot be taken back
        Assert.False(Use(vats, 0, out _));

        TanningRules.Step(state, Profile, Profile.Seconds);
        Assert.False(Use(vats, Profile.InputId, out _));
    }

    [Fact]
    public void AnEmptyHandCollectsTheResultOrTakesBackABatchThatHasNotStarted()
    {
        var vats = Ready();
        Assert.True(Use(vats, Profile.InputId, out _));
        Assert.True(Use(vats, Profile.InputId, out _));
        Assert.True(Use(vats, 0, out var back));
        Assert.Equal(new VatUse(false, Profile.InputId, 2), back);
        Assert.False(Use(vats, 0, out _));

        var done = Loaded(2);
        TanningRules.Step(State(done), Profile, Profile.Seconds);
        Assert.True(Use(done, 0, out var leather));
        Assert.Equal(new VatUse(false, Profile.ResultId, 2 * Profile.ResultPerInput), leather);
        Assert.True(State(done).IsEmpty);
    }

    [Fact]
    public void AGoneVatDropsItsBatchTanninAndResult()
    {
        var vats = Loaded(2);
        State(vats).Leather = 3;
        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        vats.Update(0.1f, manager, drops);
        Assert.Equal(2, drops.Drops.Count(d => d.BlockId == Profile.InputId));
        Assert.Equal(2, drops.Drops.Count(d => d.BlockId == Profile.TanninId));
        Assert.Equal(3, drops.Drops.Count(d => d.BlockId == Profile.ResultId));
        Assert.Null(vats.StateAt(4, 2, 4));
    }

    [Fact]
    public void AnEmptyVatIsNotSavedAndAFullOneSurvivesASaveAndTheWire()
    {
        Assert.Empty(Ready().Capture());

        var vats = Loaded(2);
        TanningRules.Step(State(vats), Profile, Profile.Seconds * 0.25f);
        var saved = new TanningVatManager();
        Assert.Equal(0, saved.Restore(vats.Capture()));
        var restored = saved.StateAt(4, 2, 4)!;
        var original = State(vats);
        Assert.Equal((original.Water, original.Bark, original.Hides, original.Progress),
            (restored.Water, restored.Bark, restored.Hides, restored.Progress));

        using var writer = new PacketWriter(NetMessage.StationState);
        vats.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new TanningVatManager();
        mirror.ReadNet(reader);
        var mirrored = mirror.StateAt(4, 2, 4)!;
        Assert.Equal((original.Water, original.Bark, original.Hides, original.Progress),
            (mirrored.Water, mirrored.Bark, mirrored.Hides, mirrored.Progress));
    }
}
