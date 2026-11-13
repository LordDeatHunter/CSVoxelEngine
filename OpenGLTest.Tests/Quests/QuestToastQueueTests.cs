using VoxelEngine.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Quests;

/// <summary>The quest-complete toast queue: one card at a time in completion order, each sliding in, resting and leaving.</summary>
public class QuestToastQueueTests
{
    private const float Lifetime =
        QuestConfig.ToastSlideInSeconds + QuestConfig.ToastHoldSeconds + QuestConfig.ToastSlideOutSeconds;

    [Fact]
    public void CardsShowOneAtATimeInCompletionOrder()
    {
        var book = Book(Quest("a", null, Break("stone")), Quest("b", null, Break("dirt")));
        var toasts = new QuestToastQueue();
        toasts.Push(book.Of("a"));
        toasts.Push(book.Of("b"));

        Assert.Same(book.Of("a"), toasts.Step(0f));
        Assert.Null(toasts.Step(Lifetime * 0.5f));
        Assert.Same(book.Of("a"), toasts.Current);
        Assert.Same(book.Of("b"), toasts.Step(Lifetime));
        Assert.Null(toasts.Step(Lifetime));
        Assert.Null(toasts.Current);
    }

    [Fact]
    public void ACardSlidesInRestsOpaqueAndLeavesOffscreen()
    {
        var toasts = new QuestToastQueue();
        toasts.Push(Book(Quest("a", null, Break("stone"))).Of("a"));
        toasts.Step(0f);
        Assert.Equal(1f, toasts.Slide);

        toasts.Step(QuestConfig.ToastSlideInSeconds + QuestConfig.ToastHoldSeconds * 0.5f);
        Assert.Equal(0f, toasts.Slide);
        Assert.Equal(1f, toasts.Alpha);

        toasts.Step(QuestConfig.ToastHoldSeconds * 0.5f + QuestConfig.ToastSlideOutSeconds * 0.999f);
        Assert.True(toasts.Slide > 0.99f);
        Assert.True(toasts.Alpha < 0.01f);
    }
}
