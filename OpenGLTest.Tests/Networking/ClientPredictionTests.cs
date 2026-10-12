using System.Collections.Generic;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// A client's predictions against the server's answers, which arrive in order but late: a stale
/// correction or broadcast never overwrites a newer prediction, every value held back lands once
/// that prediction is answered, and an inventory snapshot answering an older intent never undoes
/// a newer intent's local change.
/// </summary>
public sealed class ClientPredictionTests
{
    private static NetCellChange Cell(byte id) => new(4, 70, -2, id, 0);

    [Fact]
    public void ACorrectionForAnOlderIntentNeverOverwritesANewerPrediction()
    {
        var prediction = new ClientPrediction();

        // Placed stone (intent 1), then mined it again (intent 2) before the server answered either
        prediction.Stage(4, 70, -2);
        var placed = prediction.Commit(predictsInventory: true);
        prediction.Stage(4, 70, -2);
        var mined = prediction.Commit(predictsInventory: false);
        Assert.True(mined > placed);

        // The answer to the placement (stone there) is older than the mining prediction: held back
        Assert.False(prediction.Offer(Cell(1), placed));
        var apply = new List<NetCellChange>();
        prediction.Resolve(placed, apply);
        Assert.Empty(apply);

        // The answer to the mining (air, refused or not) is the newest: it applies and settles
        Assert.True(prediction.Offer(Cell(0), mined));
        prediction.Resolve(mined, apply);
        Assert.Empty(apply);
        Assert.Equal(0, prediction.PendingCellCount);
    }

    [Fact]
    public void ABroadcastForAPredictedCellWaitsForThePredictionsAnswer()
    {
        var prediction = new ClientPrediction();
        prediction.Stage(4, 70, -2);
        var seq = prediction.Commit(predictsInventory: false);

        // Someone else's write to the cell arrives before the server answered ours: held back
        Assert.False(prediction.Offer(Cell(9), null));
        Assert.False(prediction.Offer(Cell(8), null));

        // A cell nobody predicted applies at once
        Assert.True(prediction.Offer(new NetCellChange(0, 0, 0, 3, 0), null));

        // Once answered, the newest value held back for the cell lands
        var apply = new List<NetCellChange>();
        prediction.Resolve(seq, apply);
        Assert.Equal([Cell(8)], apply);

        // And from then on the cell takes broadcasts directly again
        Assert.True(prediction.Offer(Cell(7), null));
    }

    [Fact]
    public void AnInventorySnapshotOlderThanTheNewestInventoryChangeIsDropped()
    {
        var prediction = new ClientPrediction();
        // The join snapshot answers nothing yet and is current
        Assert.True(prediction.AcceptsInventory(0));

        var spent = prediction.Commit(predictsInventory: true);
        var swung = prediction.Commit(predictsInventory: false);
        var thrown = prediction.Commit(predictsInventory: true);

        // A pickup or the answer to the first spend predates the throw the client already showed
        Assert.False(prediction.AcceptsInventory(spent));
        Assert.False(prediction.AcceptsInventory(swung));
        // The answer to the throw (or anything after it) is the truth again
        Assert.True(prediction.AcceptsInventory(thrown));
        Assert.True(prediction.AcceptsInventory(thrown + 1));
    }
}
