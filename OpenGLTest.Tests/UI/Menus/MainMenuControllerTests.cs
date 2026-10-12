using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.UI;
using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.UI.Menus;

public sealed class MainMenuControllerTests : IDisposable
{
    private static readonly Vector2 Screen = UiCanvas.ReferenceSize;

    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _serverPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    private readonly WorldStore _store;
    private readonly ServerStore _servers;
    private long _now = 1_000;

    public MainMenuControllerTests()
    {
        // A strictly advancing clock so the store's most-recent-first order is deterministic.
        _store = new WorldStore(_root) { Clock = () => _now++ };
        _servers = new ServerStore(_serverPath) { Clock = () => _now++ };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        if (File.Exists(_serverPath))
            File.Delete(_serverPath);
    }

    private MainMenuController NewController(Func<int>? random = null) =>
        new(_store, _servers, random ?? (() => throw new InvalidOperationException("no random seed expected")));

    private static void TypeText(MainMenuController controller, string text)
    {
        foreach (var c in text)
            controller.Type(c);
    }

    private static MainMenuHit Hit(MainMenuHitKind kind, int row = -1) => new(kind, row);

    [Fact]
    public void StartOpensTheWorldsListAndBackOrEscapeReturns()
    {
        _store.Create("One", 1);
        _store.Create("Two", 2);
        var controller = NewController();

        controller.Click(Hit(MainMenuHitKind.Start));
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.Equal(_store.List().Select(w => w.Slug), controller.View.Worlds.Select(w => w.Slug));

        controller.Click(Hit(MainMenuHitKind.Back));
        Assert.Equal(MainMenuPage.Main, controller.View.Page);

        controller.Click(Hit(MainMenuHitKind.Start));
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Main, controller.View.Page);

        Assert.Equal(MainMenuAction.None, controller.Key(MenuKey.Escape));
        Assert.Equal(MainMenuPage.Main, controller.View.Page);
    }

    [Fact]
    public void ExitOnTheMainPageAsksProgramToExit()
    {
        var controller = NewController();
        Assert.Equal(MainMenuAction.Exit, controller.Click(Hit(MainMenuHitKind.Exit)));
    }

    [Fact]
    public void CreateWithEmptyFieldsMakesAWorldTheStoreNamed()
    {
        var controller = NewController(() => 4242);
        controller.Click(Hit(MainMenuHitKind.Start));
        controller.Click(Hit(MainMenuHitKind.Create));
        Assert.Equal(MainMenuPage.Create, controller.View.Page);
        Assert.Equal(MenuField.Name, controller.View.Focus);

        Assert.Equal(MainMenuAction.Play, controller.Click(Hit(MainMenuHitKind.Confirm)));
        var request = controller.TakePlayRequest();
        Assert.NotNull(request);
        Assert.True(controller.PlayRequestIsNew);
        Assert.Equal(WorldNames.Normalize(""), request!.Name);
        Assert.Equal(4242, request.Seed);
        Assert.Contains(_store.List(), w => w.Slug == request.Slug);

        Assert.Null(controller.TakePlayRequest());
    }

    [Fact]
    public void TypedNameAndSeedReachTheStore()
    {
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Start));

        // No field has focus on the worlds page, so typing there changes nothing.
        TypeText(controller, "junk");
        Assert.True(controller.View.Name.IsEmpty);
        Assert.True(controller.View.Seed.IsEmpty);

        controller.Click(Hit(MainMenuHitKind.Create));
        TypeText(controller, "Home");
        controller.Key(MenuKey.Tab);
        Assert.Equal(MenuField.Seed, controller.View.Focus);
        TypeText(controller, "42");

        Assert.Equal(MainMenuAction.Play, controller.Key(MenuKey.Enter));
        var request = controller.TakePlayRequest()!;
        Assert.Equal("Home", request.Name);
        Assert.Equal(WorldSeedParser.Parse("42", () => throw new InvalidOperationException()), request.Seed);
    }

    [Fact]
    public void BackspaceEditsOnlyTheFocusedField()
    {
        var controller = NewController(() => 0);
        controller.Click(Hit(MainMenuHitKind.Start));
        controller.Click(Hit(MainMenuHitKind.Create));
        TypeText(controller, "ab");
        controller.Click(Hit(MainMenuHitKind.SeedField));
        TypeText(controller, "12");

        controller.Key(MenuKey.Backspace);

        Assert.Equal("ab", controller.View.Name.Text);
        Assert.Equal("1", controller.View.Seed.Text);
    }

    [Fact]
    public void PlayOnARowRequestsThatExistingWorld()
    {
        _store.Create("One", 1);
        _store.Create("Two", 2);
        var controller = NewController();
        controller.OpenWorlds();

        Assert.Equal(MainMenuAction.Play, controller.Click(Hit(MainMenuHitKind.Play, 1)));
        var request = controller.TakePlayRequest()!;
        Assert.Equal(controller.View.Worlds[1].Slug, request.Slug);
        Assert.False(controller.PlayRequestIsNew);

        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.Play, 7)));
        Assert.Null(controller.PlayRequest);
    }

    [Fact]
    public void EditRenamesUniquelyAndKeepsTheFolder()
    {
        var a = _store.Create("A", 1);
        _store.Create("B", 2);
        var controller = NewController();
        controller.OpenWorlds();
        var row = controller.View.Worlds.FindIndex(w => w.Name == "B");
        var b = controller.View.Worlds[row];
        var slug = b.Slug;
        var directory = b.Directory;

        controller.Click(Hit(MainMenuHitKind.Edit, row));
        Assert.Equal(MainMenuPage.Edit, controller.View.Page);
        Assert.Equal(b.Name, controller.View.Name.Text);

        controller.View.Name.Clear();
        TypeText(controller, a.Name);
        controller.Key(MenuKey.Enter);

        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        var renamed = _store.List().Single(w => w.Slug == slug);
        Assert.Equal(directory, renamed.Directory);
        Assert.False(string.Equals(a.Name, renamed.Name, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(a.Name, renamed.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteNeedsTheConfirmClick()
    {
        _store.Create("One", 1);
        _store.Create("Two", 2);
        var controller = NewController();
        controller.OpenWorlds();
        var before = _store.List().Count;

        controller.Click(Hit(MainMenuHitKind.Delete, 0));
        Assert.Equal(MainMenuPage.ConfirmDelete, controller.View.Page);
        controller.Key(MenuKey.Enter);
        Assert.Equal(MainMenuPage.ConfirmDelete, controller.View.Page);
        Assert.Equal(before, _store.List().Count);

        controller.Click(Hit(MainMenuHitKind.CancelDelete));
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.Equal(before, controller.View.Worlds.Count);
        Assert.Equal(before, _store.List().Count);

        controller.Click(Hit(MainMenuHitKind.Delete, 0));
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.Equal(before, _store.List().Count);

        var doomed = controller.View.Worlds[0].Slug;
        controller.Click(Hit(MainMenuHitKind.Delete, 0));
        controller.Click(Hit(MainMenuHitKind.ConfirmDelete));
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.Equal(before - 1, controller.View.Worlds.Count);
        Assert.DoesNotContain(_store.List(), w => w.Slug == doomed);
    }

    [Fact]
    public void EscapeDiscardsUnsavedEdits()
    {
        _store.Create("Kept", 1);
        var controller = NewController(() => 0);
        controller.OpenWorlds();
        var count = _store.List().Count;

        controller.Click(Hit(MainMenuHitKind.Create));
        TypeText(controller, "x");
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.Equal(count, _store.List().Count);

        var name = controller.View.Worlds[0].Name;
        controller.Click(Hit(MainMenuHitKind.Edit, 0));
        TypeText(controller, "zzz");
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.Equal(name, _store.List()[0].Name);
    }

    [Fact]
    public void LockedWorldsRefusePlayAndCreateButShowTheNotice()
    {
        _store.Create("One", 1);
        var controller = NewController(() => 0);
        controller.LockWorlds("locked");
        controller.OpenWorlds();
        Assert.Equal("locked", controller.View.Notice);

        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.Play, 0)));
        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.Create)));
        Assert.Null(controller.PlayRequest);
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);

        controller.Click(Hit(MainMenuHitKind.Back));
        Assert.Equal(MainMenuPage.Main, controller.View.Page);

        var unlocked = NewController();
        unlocked.ShowNotice("hello");
        unlocked.Click(Hit(MainMenuHitKind.Settings));
        Assert.Equal("hello", unlocked.View.Notice);
        unlocked.OpenWorlds();
        Assert.Null(unlocked.View.Notice);
    }

    /// <summary>
    /// PLAY refuses a world written by another save format with a notice and no request, while a
    /// world at the current format plays; the refused row can still be edited and deleted.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void PlayRefusesAWorldOfAnotherSaveFormat(int versionOffset)
    {
        var controller = NewController();
        controller.OpenWorlds();
        var otherVersion = WorldStore.CurrentVersion + versionOffset;
        controller.View.Worlds.Add(new WorldInfo("old", "Old", 1, 0, 0, otherVersion, "old"));
        controller.View.Worlds.Add(new WorldInfo("new", "New", 2, 0, 0, WorldStore.CurrentVersion, "new"));

        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.Play, 0)));
        Assert.Null(controller.PlayRequest);
        Assert.NotNull(controller.View.Notice);
        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);

        controller.Click(Hit(MainMenuHitKind.Delete, 0));
        Assert.Equal(MainMenuPage.ConfirmDelete, controller.View.Page);
        controller.Key(MenuKey.Escape);

        Assert.Equal(MainMenuAction.Play, controller.Click(Hit(MainMenuHitKind.Play, 1)));
        Assert.Equal("new", controller.TakePlayRequest()!.Slug);
    }

    [Fact]
    public void AStoreErrorShowsANoticeInsteadOfThrowing()
    {
        _store.Create("One", 1);
        var controller = NewController();
        controller.OpenWorlds();
        // A world whose folder lies outside the root is refused by the store (ArgumentException).
        // (A fresh, non-existent path, so nothing real is at stake even if the check regressed.)
        var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        controller.View.Worlds[0] = new WorldInfo("x", "X", 0, 0, 0, 1, outside);

        controller.Click(Hit(MainMenuHitKind.Delete, 0));
        controller.Click(Hit(MainMenuHitKind.ConfirmDelete));

        Assert.Equal(MainMenuPage.Worlds, controller.View.Page);
        Assert.NotNull(controller.View.Notice);
        Assert.Single(_store.List());
    }

    [Fact]
    public void ScrollClampsToTheListAndOnlyOnTheWorldsPage()
    {
        var controller = NewController();
        var rows = (int)MathF.Ceiling(MainMenuLayout.ListHeight(Screen) / MainMenuLayout.RowHeight) + 3;
        controller.OpenWorlds();
        for (var i = 0; i < rows; i++)
            controller.View.Worlds.Add(new WorldInfo($"w{i}", $"World {i}", i, 0, 0, 1, $"dir{i}"));

        controller.Scroll(1000f, Screen);
        Assert.Equal(MainMenuLayout.ContentHeight(rows) - MainMenuLayout.ListHeight(Screen),
            controller.View.ScrollOffset, 3);

        controller.Scroll(-1000f, Screen);
        Assert.Equal(0f, controller.View.ScrollOffset);

        controller.OpenMain();
        controller.View.ScrollOffset = 0f;
        controller.Scroll(5f, Screen);
        Assert.Equal(0f, controller.View.ScrollOffset);
    }

    [Fact]
    public void MultiplayerOpensTheServerListAndBackReturns()
    {
        _servers.Add("Alpha", "10.0.0.1", ServerAddress.DefaultPort);
        _servers.Add("Beta", "example.com", 30000);
        var controller = NewController();

        controller.Click(Hit(MainMenuHitKind.Multiplayer));
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
        Assert.Equal(_servers.List().Select(s => s.Id), controller.View.Servers.Select(s => s.Id));

        controller.Click(Hit(MainMenuHitKind.Back));
        Assert.Equal(MainMenuPage.Main, controller.View.Page);
    }

    [Fact]
    public void AddServerStoresTheAddressAndReturnsToTheList()
    {
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Multiplayer));
        controller.Click(Hit(MainMenuHitKind.AddServer));
        Assert.Equal(MainMenuPage.AddServer, controller.View.Page);

        TypeText(controller, "My Server");
        controller.Click(Hit(MainMenuHitKind.AddressField));
        TypeText(controller, "192.168.0.5:25599");
        controller.Click(Hit(MainMenuHitKind.Confirm));

        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
        var saved = Assert.Single(_servers.List());
        Assert.Equal("My Server", saved.Name);
        Assert.Equal("192.168.0.5", saved.Host);
        Assert.Equal(25599, saved.Port);
        Assert.Null(controller.TakeConnectRequest());
    }

    [Fact]
    public void AddServerWithAnEmptyAddressKeepsThePageAndNotices()
    {
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Multiplayer));
        controller.Click(Hit(MainMenuHitKind.AddServer));
        controller.Click(Hit(MainMenuHitKind.Confirm));

        Assert.Equal(MainMenuPage.AddServer, controller.View.Page);
        Assert.Equal(MainMenuController.BadAddressNotice, controller.View.Notice);
        Assert.Empty(_servers.List());
    }

    [Fact]
    public void DirectConnectReturnsTheTypedTarget()
    {
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Multiplayer));
        controller.Click(Hit(MainMenuHitKind.DirectConnect));
        Assert.Equal(MainMenuPage.DirectConnect, controller.View.Page);

        TypeText(controller, "play.example.net");
        Assert.Equal(MainMenuAction.Connect, controller.Click(Hit(MainMenuHitKind.Connect)));

        var target = controller.TakeConnectRequest();
        Assert.True(target.HasValue);
        Assert.Equal("play.example.net", target!.Value.Address.Host);
        Assert.Equal(ServerAddress.DefaultPort, target.Value.Address.Port);
        Assert.Null(controller.TakeConnectRequest());
    }

    [Fact]
    public void ConnectOnARowCarriesTheServerAndTouchesIt()
    {
        _servers.Add("Alpha", "10.0.0.1", 25566);
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Multiplayer));

        Assert.Equal(MainMenuAction.Connect, controller.Click(Hit(MainMenuHitKind.Connect, 0)));
        var target = controller.TakeConnectRequest();
        Assert.True(target.HasValue);
        Assert.Equal("10.0.0.1", target!.Value.Address.Host);
        Assert.Equal(25566, target.Value.Address.Port);
    }

    [Fact]
    public void DeletingAServerConfirmsThenRemovesIt()
    {
        _servers.Add("Alpha", "10.0.0.1", 25566);
        _servers.Add("Beta", "10.0.0.2", 25566);
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Multiplayer));

        controller.Click(Hit(MainMenuHitKind.DeleteServer, 0));
        Assert.Equal(MainMenuPage.ConfirmDeleteServer, controller.View.Page);
        controller.Click(Hit(MainMenuHitKind.CancelDeleteServer));
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
        Assert.Equal(2, _servers.List().Count);

        controller.Click(Hit(MainMenuHitKind.DeleteServer, 0));
        controller.Click(Hit(MainMenuHitKind.ConfirmDeleteServer));
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
        Assert.Single(_servers.List());
    }

    [Fact]
    public void EscapeOnAServerFormStepsBackToTheServerList()
    {
        var controller = NewController();
        controller.Click(Hit(MainMenuHitKind.Multiplayer));
        controller.Click(Hit(MainMenuHitKind.AddServer));
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);

        controller.Click(Hit(MainMenuHitKind.DirectConnect));
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
    }

    [Fact]
    public void OpeningAServerFormDropsTheListsNoticeButNeverTheWorldsLock()
    {
        var controller = NewController();
        controller.OpenMultiplayer();
        controller.ShowNotice("Lost connection to the server");
        controller.Click(Hit(MainMenuHitKind.AddServer));
        Assert.Null(controller.View.Notice);

        var locked = NewController();
        locked.LockWorlds("locked");
        locked.OpenMultiplayer();
        Assert.Equal("locked", locked.View.Notice);
        locked.Click(Hit(MainMenuHitKind.DirectConnect));
        Assert.Equal("locked", locked.View.Notice);
        locked.Key(MenuKey.Escape);
        locked.Key(MenuKey.Escape);
        locked.OpenWorlds();
        Assert.Equal("locked", locked.View.Notice);
    }
}
