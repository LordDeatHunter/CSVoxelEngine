using System.Numerics;
using VoxelEngine.Quests;
using VoxelEngine.Saves;
using VoxelEngine.UI.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.UI;

/// <summary>The quest book's camera and hit rules: zoom about the pointer, pan follows the pointer, a short press is a
/// click, and only visible quests are hit.</summary>
public class QuestBookLayoutTests
{
    private static readonly Vector2 Canvas = new(1920, 1080);

    [Fact]
    public void ZoomingKeepsTheGridPointUnderThePointer()
    {
        var camera = new QuestViewData { PanX = 3f, PanY = -2f, Zoom = 1f };
        var pointer = new Vector2(400, 900);
        var before = QuestBookLayout.ToGrid(pointer, camera, Canvas);

        QuestBookLayout.ZoomAbout(camera, pointer, Canvas, 1.5f);

        Assert.True(Vector2.Distance(before, QuestBookLayout.ToGrid(pointer, camera, Canvas)) < 1e-4f);
        Assert.Equal(1.5f, camera.Zoom, 4);
    }

    [Fact]
    public void ZoomStaysInItsRange()
    {
        var camera = new QuestViewData();
        for (var i = 0; i < 50; i++) QuestBookLayout.ZoomAbout(camera, Canvas * 0.5f, Canvas, 2f);
        Assert.Equal(QuestConfig.MaxZoom, camera.Zoom);
        for (var i = 0; i < 50; i++) QuestBookLayout.ZoomAbout(camera, Canvas * 0.5f, Canvas, 0.5f);
        Assert.Equal(QuestConfig.MinZoom, camera.Zoom);
    }

    [Fact]
    public void ADragPansTheContentWithThePointerButAShortPressDoesNot()
    {
        var view = new QuestBookView();
        view.Open(new QuestViewData { Zoom = 1.3f });
        var start = new Vector2(800, 500);
        var grabbed = QuestBookLayout.ToGrid(start, view.Camera, Canvas);

        view.BeginPress(start);
        view.TrackPress(start + new Vector2(QuestConfig.DragThreshold * 0.5f, 0f));
        Assert.False(view.Dragging);
        Assert.Equal(0f, view.Camera.PanX);

        var end = start + new Vector2(150, -90);
        view.TrackPress(end);
        Assert.True(view.Dragging);
        Assert.True(Vector2.Distance(grabbed, QuestBookLayout.ToGrid(end, view.Camera, Canvas)) < 1e-4f);
    }

    [Fact]
    public void OnlyVisibleQuestsAreHit()
    {
        var root = Quest("root", null, Break("dirt"));
        var child = Quest("child", ["root"], Break("stone"));
        child.Pos = [3, 0];
        var book = Book(root, child);
        var log = new QuestLog(book);
        var camera = new QuestViewData();
        var childPoint = QuestBookLayout.ToCanvas(book.Of("child").Position, camera, Canvas);

        Assert.Same(book.Of("root"), QuestBookLayout.NodeAt(log, camera, Canvas, QuestBookLayout.ToCanvas(book.Of("root").Position, camera, Canvas)));
        Assert.Null(QuestBookLayout.NodeAt(log, camera, Canvas, childPoint));
        log.OnBroke(Id("dirt"));
        Assert.Same(book.Of("child"), QuestBookLayout.NodeAt(log, camera, Canvas, childPoint));
    }
}
