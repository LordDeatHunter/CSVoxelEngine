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
    public void PlayAndHostRefuseUnknownSavedTerrainBeforeRequestingAWorld()
    {
        var created = _store.Create("Unknown terrain", 17);
        var path = Path.Combine(created.Directory, WorldStore.MetadataFileName);
        var original = """{"Name":"Unknown terrain","Seed":17,"Version":2,"terrain":99}""";
        File.WriteAllText(path, original);
        var controller = NewController();

        controller.OpenWorlds();
        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.Play, 0)));
        Assert.Null(controller.PlayRequest);
        Assert.Contains("terrain", controller.View.Notice!, StringComparison.OrdinalIgnoreCase);

        controller.OpenHost();
        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.HostWorld, 0)));
        Assert.Null(controller.HostRequest);
        Assert.Contains("terrain", controller.View.Notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllText(path));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateDoesNotRequestPlayOrHostWhenWorldNamesCannotBeListed(bool host)
    {
        _store.Create("Home", 1);
        var controller = NewController(() => 42);
        if (host)
            controller.OpenHost();
        else
            controller.OpenWorlds();
        controller.Click(Hit(MainMenuHitKind.Create));
        TypeText(controller, "Home");

        var failNext = true;
        _store.EnumerateDirectories = path =>
        {
            if (!failNext) return Directory.GetDirectories(path);
            failNext = false;
            throw new IOException("injected enumeration failure");
        };

        var action = controller.Key(MenuKey.Enter);
        var playRequest = controller.TakePlayRequest();
        var hostRequest = controller.TakeHostRequest();
        var worlds = _store.List();
        var namesUnique = worlds.Select(world => world.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == worlds.Count;

        Assert.True(action == MainMenuAction.None && playRequest == null && hostRequest == null
                    && controller.View.Notice == MainMenuController.StoreErrorNotice && namesUnique,
            $"host={host}; action={action}; play={playRequest?.Name ?? "none"}; " +
            $"hostRequest={hostRequest?.World.Name ?? "none"}; notice={controller.View.Notice ?? "none"}; " +
            $"names=[{string.Join(",", worlds.Select(world => world.Name))}]");
    }

    [Fact]
    public void RenameShowsStoreErrorAndKeepsTheExistingWorldWhenNameListFails()
    {
        _store.Create("A", 1);
        _store.Create("B", 2);
        var controller = NewController();
        controller.OpenWorlds();
        var row = controller.View.Worlds.FindIndex(world => world.Name == "B");
        var selected = controller.View.Worlds[row];
        var metadataPath = Path.Combine(selected.Directory, WorldStore.MetadataFileName);
        var metadataBefore = File.ReadAllBytes(metadataPath);
        controller.Click(Hit(MainMenuHitKind.Edit, row));
        controller.View.Name.Clear();
        TypeText(controller, "A");

        var failNext = true;
        _store.EnumerateDirectories = path =>
        {
            if (!failNext) return Directory.GetDirectories(path);
            failNext = false;
            throw new IOException("injected enumeration failure");
        };

        var action = controller.Key(MenuKey.Enter);
        var worlds = _store.List();
        var metadataUnchanged = File.ReadAllBytes(metadataPath).SequenceEqual(metadataBefore);
        var namesUnique = worlds.Select(world => world.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == worlds.Count;

        Assert.True(action == MainMenuAction.None && selected.Name == "B" && metadataUnchanged
                    && namesUnique && controller.View.Page == MainMenuPage.Worlds
                    && controller.View.Notice == MainMenuController.StoreErrorNotice,
            $"action={action}; inMemoryName={selected.Name}; metadataUnchanged={metadataUnchanged}; " +
            $"names=[{string.Join(",", worlds.Select(world => world.Name))}]; notice={controller.View.Notice ?? "none"}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWorldEnumerationIsShownOnWorldAndHostPages(bool host)
    {
        _store.Create("One", 1);
        _store.EnumerateDirectories = _ => throw new IOException("injected enumeration failure");
        var controller = NewController();

        if (host)
            controller.OpenHost();
        else
            controller.OpenWorlds();

        Assert.Empty(controller.View.Worlds);
        Assert.True(controller.View.WorldListFailed);
        Assert.Equal(MainMenuController.StoreErrorNotice, controller.View.Notice);
    }

    [Fact]
    public void MissingWorldRootIsShownAsAnOrdinaryEmptyList()
    {
        var controller = NewController();

        controller.OpenWorlds();

        Assert.Empty(controller.View.Worlds);
        Assert.False(controller.View.WorldListFailed);
        Assert.Null(controller.View.Notice);
    }

    [Fact]
    public void WorldListingFailureClearsAfterTheNextSuccessfulOpen()
    {
        _store.Create("One", 1);
        _store.EnumerateDirectories = _ => throw new IOException("injected enumeration failure");
        var controller = NewController();
        controller.OpenWorlds();
        Assert.True(controller.View.WorldListFailed);

        _store.EnumerateDirectories = Directory.GetDirectories;
        controller.OpenWorlds();

        Assert.False(controller.View.WorldListFailed);
        Assert.Null(controller.View.Notice);
        Assert.Equal("One", Assert.Single(controller.View.Worlds).Name);
    }

    [Fact]
    public void WorldListingFailureDoesNotReplaceTheWorldsLockedNotice()
    {
        _store.EnumerateDirectories = _ => throw new IOException("injected enumeration failure");
        var controller = NewController();
        controller.View.WorldsLocked = true;
        controller.View.Notice = "Worlds are locked";

        controller.OpenWorlds();

        Assert.True(controller.View.WorldListFailed);
        Assert.Equal("Worlds are locked", controller.View.Notice);
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
    public void FailedTouchReadDoesNotBlockConnectOrReplaceSavedBytes()
    {
        _servers.Add("Alpha", "10.0.0.1", 25566);
        var controller = NewController();
        controller.OpenMultiplayer();
        var server = Assert.Single(controller.View.Servers);
        var original = File.ReadAllBytes(_serverPath);
        var originalLastPlayed = server.LastPlayedUnixMs;
        _servers.ReadAllText = _ => throw new IOException("injected server-list read failure");

        var action = controller.Click(Hit(MainMenuHitKind.Connect, 0));
        var request = controller.TakeConnectRequest();
        var after = File.ReadAllBytes(_serverPath);
        var bytesUnchanged = original.SequenceEqual(after);
        var timestampUnchanged = server.LastPlayedUnixMs == originalLastPlayed;
        Assert.True(action == MainMenuAction.Connect && request is { Address.Host: "10.0.0.1" }
                    && bytesUnchanged && timestampUnchanged,
            $"action={action}; connectRequest={request.HasValue}; bytesUnchanged={bytesUnchanged}; " +
            $"timestampUnchanged={timestampUnchanged}; beforeLength={original.Length}; afterLength={after.Length}");
    }

    [Fact]
    public void FailedServerListReadShowsTheStoreErrorNotice()
    {
        _servers.Add("Alpha", "10.0.0.1", 25566);
        var controller = NewController();
        _servers.ReadAllText = _ => throw new IOException("injected server-list read failure");

        controller.OpenMultiplayer();

        Assert.True(controller.View.ServerListFailed);
        Assert.Empty(controller.View.Servers);
        Assert.Equal(MainMenuController.StoreErrorNotice, controller.View.Notice);
    }

    [Fact]
    public void ReopeningServerListAfterReadRecoveryRestoresRowsAndClearsFailure()
    {
        _servers.Add("Alpha", "10.0.0.1", 25566);
        var controller = NewController();
        _servers.ReadAllText = _ => throw new IOException("injected server-list read failure");
        controller.OpenMultiplayer();
        Assert.True(controller.View.ServerListFailed);

        _servers.ReadAllText = File.ReadAllText;
        controller.OpenMultiplayer();

        Assert.False(controller.View.ServerListFailed);
        Assert.Null(controller.View.Notice);
        Assert.Equal("Alpha", Assert.Single(controller.View.Servers).Name);
    }

    [Fact]
    public void FailedServerListReadPreservesWorldsLockedNotice()
    {
        var controller = NewController();
        controller.LockWorlds("worlds are locked");
        _servers.ReadAllText = _ => throw new IOException("injected server-list read failure");

        controller.OpenMultiplayer();

        Assert.True(controller.View.ServerListFailed);
        Assert.Equal("worlds are locked", controller.View.Notice);
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
    public void HostOnARowRequestsThatWorldWithThePortAndUpnpChoice()
    {
        _store.Create("Shared", 7);
        var controller = NewController();
        controller.OpenMultiplayer();

        controller.Click(Hit(MainMenuHitKind.OpenHost));
        Assert.Equal(MainMenuPage.Host, controller.View.Page);
        Assert.Equal(_store.List().Select(w => w.Slug), controller.View.Worlds.Select(w => w.Slug));

        // The port takes digits only
        controller.Click(Hit(MainMenuHitKind.PortField));
        TypeText(controller, "p3o0:00 0");
        Assert.Equal("30000", controller.View.Port.Text);
        controller.Click(Hit(MainMenuHitKind.ToggleUpnp));

        Assert.Equal(MainMenuAction.Host, controller.Click(Hit(MainMenuHitKind.HostWorld, 0)));
        var request = controller.TakeHostRequest()!.Value;
        Assert.Equal("shared", request.World.Slug);
        Assert.Equal(30000, request.Port);
        Assert.True(request.Upnp);
        Assert.Null(controller.TakeHostRequest());

        // Back and ESC both step back to the server list
        controller.Click(Hit(MainMenuHitKind.Back));
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
        controller.Click(Hit(MainMenuHitKind.OpenHost));
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Multiplayer, controller.View.Page);
    }

    [Theory]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("", true, ServerAddress.DefaultPort)]
    [InlineData("65535", true, 65535)]
    public void AnOutOfRangePortKeepsTheHostPageUpWithANotice(string typed, bool hosts, int port)
    {
        _store.Create("World", 1);
        var controller = NewController();
        controller.OpenHost();
        controller.View.Port.Set(typed);

        var action = controller.Click(Hit(MainMenuHitKind.HostWorld, 0));

        Assert.Equal(hosts ? MainMenuAction.Host : MainMenuAction.None, action);
        Assert.Equal(MainMenuPage.Host, controller.View.Page);
        if (hosts)
            Assert.Equal(port, controller.TakeHostRequest()!.Value.Port);
        else
            Assert.Equal(MainMenuController.BadPortNotice, controller.View.Notice);
    }

    [Fact]
    public void HostRefusesAWorldOfAnotherSaveFormatAndLockedWorlds()
    {
        var controller = NewController();
        controller.OpenHost();
        controller.View.Worlds.Add(new WorldInfo("old", "Old", 1, 0, 0, WorldStore.CurrentVersion - 1, "old"));
        Assert.Equal(MainMenuAction.None, controller.Click(Hit(MainMenuHitKind.HostWorld, 0)));
        Assert.Null(controller.HostRequest);
        Assert.NotNull(controller.View.Notice);

        _store.Create("Fine", 1);
        var locked = NewController();
        locked.LockWorlds("locked");
        locked.OpenHost();
        Assert.Equal(MainMenuAction.None, locked.Click(Hit(MainMenuHitKind.HostWorld, 0)));
        locked.Click(Hit(MainMenuHitKind.Create));
        Assert.Equal(MainMenuPage.Host, locked.View.Page);
    }

    [Fact]
    public void ANewWorldFromTheHostPageIsCreatedAndHosted()
    {
        var controller = NewController(() => 99);
        controller.OpenHost();
        controller.Click(Hit(MainMenuHitKind.Create));
        Assert.Equal(MainMenuPage.Create, controller.View.Page);

        // BACK and ESC return to the page that opened the form
        controller.Click(Hit(MainMenuHitKind.Back));
        Assert.Equal(MainMenuPage.Host, controller.View.Page);
        controller.Click(Hit(MainMenuHitKind.Create));
        controller.Key(MenuKey.Escape);
        Assert.Equal(MainMenuPage.Host, controller.View.Page);

        // A bad port is caught before the world is made
        controller.View.Port.Set("99999");
        controller.Click(Hit(MainMenuHitKind.Create));
        TypeText(controller, "Lan Party");
        Assert.Equal(MainMenuAction.None, controller.Key(MenuKey.Enter));
        Assert.Empty(_store.List());
        Assert.Equal(MainMenuPage.Host, controller.View.Page);
        Assert.Equal(MainMenuController.BadPortNotice, controller.View.Notice);

        controller.View.Port.Set("");
        controller.Click(Hit(MainMenuHitKind.Create));
        TypeText(controller, "Lan Party");
        Assert.Equal(MainMenuAction.Host, controller.Key(MenuKey.Enter));
        var request = controller.TakeHostRequest()!.Value;
        Assert.Equal("Lan Party", request.World.Name);
        Assert.Equal(99, request.World.Seed);
        Assert.Single(_store.List());
        Assert.Null(controller.PlayRequest);

        // CREATE from the worlds page still plays
        controller.OpenWorlds();
        controller.Click(Hit(MainMenuHitKind.Create));
        Assert.Equal(MainMenuAction.Play, controller.Key(MenuKey.Enter));
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
