using System;
using System.IO;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.UI.Menus;

/// <summary>
/// Every rule of the main menu in one GL-free place: page transitions, the four non-character
/// keys, typing into the focused field and every <see cref="WorldStore"/> / <see cref="ServerStore"/>
/// call (list, create, rename, delete, add server). Program cannot be tested, so the whole flow is
/// pinned here and InputEvents only feeds it hits and keys while Program acts on the <see cref="MainMenuAction"/>
/// it gets back (a <see cref="MainMenuAction.Play"/> carries its world in <see cref="PlayRequest"/>,
/// a <see cref="MainMenuAction.Connect"/> its server in <see cref="ConnectRequest"/>, a
/// <see cref="MainMenuAction.Host"/> its world and port in <see cref="HostRequest"/>).
/// <para>
/// It never touches GL, the cursor or Silk. The settings page is only opened and closed here;
/// MenuInput owns that page's own clicks and slider drags through <see cref="SettingsLayout"/>,
/// shared with the pause menu. Store errors never escape: they are logged in full and shown as
/// the one-line <see cref="StoreErrorNotice"/> on the world/server page. Main thread only.
/// </para>
/// </summary>
public sealed class MainMenuController(WorldStore store, ServerStore serverStore, Func<int> randomSeed)
{
    /// <summary>The notice shown after a failed store call.</summary>
    public const string StoreErrorNotice = "Saves folder error - see the console";

    /// <summary>The notice shown when a typed server address cannot be parsed.</summary>
    public const string BadAddressNotice = "Enter an address like 192.168.1.10 or host:25566";

    /// <summary>The notice shown when the host page's port is out of range.</summary>
    public static readonly string BadPortNotice =
        $"Enter a port from {ServerAddress.MinPort} to {ServerAddress.MaxPort} (empty for {ServerAddress.DefaultPort})";

    public MainMenuView View { get; } = new();

    /// <summary>The world PLAY or CREATE asked Program to open, until <see cref="TakePlayRequest"/>.</summary>
    public WorldInfo? PlayRequest { get; private set; }

    /// <summary>Whether the latest request came from CREATE (a fresh world); readable until the next request.</summary>
    public bool PlayRequestIsNew { get; private set; }

    /// <summary>The server a CONNECT asked Program to open, until <see cref="TakeConnectRequest"/>.</summary>
    public ConnectTarget? ConnectRequest { get; private set; }

    /// <summary>The world a HOST asked Program to host, until <see cref="TakeHostRequest"/>.</summary>
    public HostTarget? HostRequest { get; private set; }

    /// <summary>Returns the pending host request and clears it.</summary>
    public HostTarget? TakeHostRequest()
    {
        var request = HostRequest;
        HostRequest = null;
        return request;
    }

    /// <summary>Returns the pending play request and clears it.</summary>
    public WorldInfo? TakePlayRequest()
    {
        var request = PlayRequest;
        PlayRequest = null;
        return request;
    }

    /// <summary>Returns the pending connect request and clears it.</summary>
    public ConnectTarget? TakeConnectRequest()
    {
        var request = ConnectRequest;
        ConnectRequest = null;
        return request;
    }

    public void OpenMain()
    {
        View.Page = MainMenuPage.Main;
        View.SelectedRow = -1;
        View.SelectedServerRow = -1;
        View.Focus = MenuField.None;
    }

    /// <summary>Re-reads the store's worlds and shows the list from the top. Clears the notice
    /// unless the worlds are locked, whose notice must stay up.</summary>
    public void OpenWorlds()
    {
        if (!View.WorldsLocked)
            View.Notice = null;
        View.Worlds.Clear();
        Guarded(() => View.Worlds.AddRange(store.List()));
        View.ScrollOffset = 0f;
        View.SelectedRow = -1;
        View.Focus = MenuField.None;
        View.Page = MainMenuPage.Worlds;
    }

    /// <summary>Re-reads the saved servers and shows the multiplayer list from the top. Clears the
    /// notice unless the worlds are locked, whose notice must stay up for the worlds page.</summary>
    public void OpenMultiplayer()
    {
        if (!View.WorldsLocked)
            View.Notice = null;
        View.Servers.Clear();
        View.ServerListFailed = false;
        Guarded(() =>
        {
            if (!serverStore.TryList(out var servers))
            {
                View.ServerListFailed = true;
                SetErrorNotice();
                return;
            }

            View.Servers.AddRange(servers);
        });
        View.ServerScrollOffset = 0f;
        View.SelectedServerRow = -1;
        View.Focus = MenuField.None;
        View.Page = MainMenuPage.Multiplayer;
    }

    /// <summary>Re-reads the store's worlds and shows the host page (the worlds with a HOST button each, the port and
    /// the UPnP toggle) from the top. The port and the toggle keep what they held. Clears the notice unless the worlds
    /// are locked.</summary>
    public void OpenHost()
    {
        if (!View.WorldsLocked)
            View.Notice = null;
        View.Worlds.Clear();
        Guarded(() => View.Worlds.AddRange(store.List()));
        View.ScrollOffset = 0f;
        View.SelectedRow = -1;
        View.Focus = MenuField.None;
        View.CreateThenHost = false;
        View.Page = MainMenuPage.Host;
    }

    public void OpenSettings() => View.Page = MainMenuPage.Settings;

    /// <summary>Back from the settings page to the main page; InputEvents clears the slider drag itself.</summary>
    public void CloseSettings() => View.Page = MainMenuPage.Main;

    /// <summary>Refuses PLAY and CREATE for the rest of the process (a teardown left the seed
    /// locked, so <see cref="WorldSeed.Apply"/> would throw) and keeps <paramref name="notice"/> up.</summary>
    public void LockWorlds(string notice)
    {
        View.WorldsLocked = true;
        View.Notice = notice;
    }

    /// <summary>Shows a one-line notice under the current list until the next open of that list.</summary>
    public void ShowNotice(string? notice) => View.Notice = notice;

    public MainMenuAction Click(MainMenuHit hit)
    {
        switch (View.Page)
        {
            case MainMenuPage.Main:
                switch (hit.Kind)
                {
                    case MainMenuHitKind.Start: OpenWorlds(); break;
                    case MainMenuHitKind.Multiplayer: OpenMultiplayer(); break;
                    case MainMenuHitKind.Settings: OpenSettings(); break;
                    case MainMenuHitKind.Exit: return MainMenuAction.Exit;
                }
                return MainMenuAction.None;

            case MainMenuPage.Worlds:
                return ClickWorlds(hit);

            case MainMenuPage.Create:
                switch (hit.Kind)
                {
                    case MainMenuHitKind.NameField: View.Focus = MenuField.Name; break;
                    case MainMenuHitKind.SeedField: View.Focus = MenuField.Seed; break;
                    case MainMenuHitKind.Confirm: return Confirm();
                    case MainMenuHitKind.Back: LeaveCreate(); break;
                }
                return MainMenuAction.None;

            case MainMenuPage.Edit:
                switch (hit.Kind)
                {
                    case MainMenuHitKind.NameField: View.Focus = MenuField.Name; break;
                    case MainMenuHitKind.Confirm: return Confirm();
                    case MainMenuHitKind.Back: OpenWorlds(); break;
                }
                return MainMenuAction.None;

            case MainMenuPage.ConfirmDelete:
                switch (hit.Kind)
                {
                    case MainMenuHitKind.ConfirmDelete: ConfirmDelete(); break;
                    case MainMenuHitKind.CancelDelete: CancelDelete(); break;
                }
                return MainMenuAction.None;

            case MainMenuPage.Multiplayer:
                return ClickMultiplayer(hit);

            case MainMenuPage.AddServer:
                return ClickAddServer(hit);

            case MainMenuPage.DirectConnect:
                return ClickDirectConnect(hit);

            case MainMenuPage.ConfirmDeleteServer:
                switch (hit.Kind)
                {
                    case MainMenuHitKind.ConfirmDeleteServer: ConfirmDeleteServer(); break;
                    case MainMenuHitKind.CancelDeleteServer: CancelDeleteServer(); break;
                }
                return MainMenuAction.None;

            case MainMenuPage.Host:
                return ClickHost(hit);

            default:
                return MainMenuAction.None;
        }
    }

    public MainMenuAction Key(MenuKey key)
    {
        switch (key)
        {
            case MenuKey.Backspace:
                View.FocusedField?.Backspace();
                return MainMenuAction.None;

            case MenuKey.Tab:
                if (View.Page == MainMenuPage.Create)
                    View.Focus = View.Focus == MenuField.Name ? MenuField.Seed : MenuField.Name;
                else if (View.Page == MainMenuPage.AddServer)
                    View.Focus = View.Focus == MenuField.ServerName ? MenuField.Address : MenuField.ServerName;
                return MainMenuAction.None;

            case MenuKey.Enter:
                // Enter never confirms a delete: that popup needs its own click.
                return View.Page switch
                {
                    MainMenuPage.Create or MainMenuPage.Edit => Confirm(),
                    MainMenuPage.AddServer => ClickAddServer(new MainMenuHit(MainMenuHitKind.Confirm)),
                    MainMenuPage.DirectConnect => ClickDirectConnect(new MainMenuHit(MainMenuHitKind.Connect)),
                    _ => MainMenuAction.None,
                };

            case MenuKey.Escape:
                switch (View.Page)
                {
                    case MainMenuPage.ConfirmDelete: CancelDelete(); break;
                    case MainMenuPage.ConfirmDeleteServer: CancelDeleteServer(); break;
                    case MainMenuPage.Create: LeaveCreate(); break;
                    case MainMenuPage.Edit: OpenWorlds(); break; // unsaved edits are discarded
                    case MainMenuPage.AddServer:
                    case MainMenuPage.DirectConnect:
                    case MainMenuPage.Host: OpenMultiplayer(); break; // one page back, like BACK
                    case MainMenuPage.Multiplayer: OpenMain(); break;
                    case MainMenuPage.Worlds:
                    case MainMenuPage.Settings: OpenMain(); break;
                    // Main: ESC never exits the game.
                }
                return MainMenuAction.None;

            default:
                return MainMenuAction.None;
        }
    }

    /// <summary>Focuses the text field a press landed on and returns it; null when the hit is not a field.</summary>
    public TextFieldState? FocusFieldAt(MainMenuHit hit)
    {
        switch (hit.Kind)
        {
            case MainMenuHitKind.NameField: View.Focus = MenuField.Name; break;
            case MainMenuHitKind.SeedField: View.Focus = MenuField.Seed; break;
            case MainMenuHitKind.ServerNameField: View.Focus = MenuField.ServerName; break;
            case MainMenuHitKind.AddressField: View.Focus = MenuField.Address; break;
            case MainMenuHitKind.PortField: View.Focus = MenuField.Port; break;
            default: return null;
        }
        return View.FocusedField;
    }

    /// <summary>Types one character into the focused field; refused characters and an unfocused page do nothing.</summary>
    public void Type(char c) => View.FocusedField?.TryAppend(c);

    /// <summary>Scrolls the active list by <paramref name="rows"/> rows, clamped to the list; other pages ignore it.</summary>
    public void Scroll(float rows, Vector2 screenSize)
    {
        switch (View.Page)
        {
            case MainMenuPage.Worlds:
            case MainMenuPage.Host:
                View.ScrollOffset = MainMenuLayout.ClampedOffset(
                    View.ScrollOffset + rows * MainMenuLayout.RowHeight, View.Worlds.Count, screenSize);
                break;
            case MainMenuPage.Multiplayer:
                View.ServerScrollOffset = MainMenuLayout.ClampedOffset(
                    View.ServerScrollOffset + rows * MainMenuLayout.RowHeight, View.Servers.Count, screenSize);
                break;
        }
    }

    private MainMenuAction ClickWorlds(MainMenuHit hit)
    {
        switch (hit.Kind)
        {
            case MainMenuHitKind.Play when IsRow(hit.Row):
                if (View.WorldsLocked)
                    return MainMenuAction.None;
                var world = View.Worlds[hit.Row];
                if (RefusesOldFormat(world))
                    return MainMenuAction.None;
                PlayRequest = world;
                PlayRequestIsNew = false;
                return MainMenuAction.Play;

            case MainMenuHitKind.Edit when IsRow(hit.Row):
                View.SelectedRow = hit.Row;
                View.Name.Set(View.Worlds[hit.Row].Name);
                View.Focus = MenuField.Name;
                View.Page = MainMenuPage.Edit;
                return MainMenuAction.None;

            case MainMenuHitKind.Delete when IsRow(hit.Row):
                View.SelectedRow = hit.Row;
                View.Page = MainMenuPage.ConfirmDelete;
                return MainMenuAction.None;

            case MainMenuHitKind.Create:
                OpenCreate(thenHost: false);
                return MainMenuAction.None;

            case MainMenuHitKind.Back:
                OpenMain();
                return MainMenuAction.None;

            default:
                return MainMenuAction.None;
        }
    }

    private MainMenuAction ClickMultiplayer(MainMenuHit hit)
    {
        switch (hit.Kind)
        {
            case MainMenuHitKind.Connect when IsServerRow(hit.Row):
            {
                var server = View.Servers[hit.Row];
                Guarded(() => serverStore.TouchLastPlayed(server));
                ConnectRequest = new ConnectTarget(server.Name, server.Address);
                return MainMenuAction.Connect;
            }

            case MainMenuHitKind.DeleteServer when IsServerRow(hit.Row):
                View.SelectedServerRow = hit.Row;
                View.Page = MainMenuPage.ConfirmDeleteServer;
                return MainMenuAction.None;

            case MainMenuHitKind.AddServer:
                ClearPageNotice();
                View.ServerName.Clear();
                View.Address.Clear();
                View.Focus = MenuField.ServerName;
                View.Page = MainMenuPage.AddServer;
                return MainMenuAction.None;

            case MainMenuHitKind.DirectConnect:
                ClearPageNotice();
                View.Address.Clear();
                View.Focus = MenuField.Address;
                View.Page = MainMenuPage.DirectConnect;
                return MainMenuAction.None;

            case MainMenuHitKind.OpenHost:
                OpenHost();
                return MainMenuAction.None;

            case MainMenuHitKind.Back:
                OpenMain();
                return MainMenuAction.None;

            default:
                return MainMenuAction.None;
        }
    }

    private MainMenuAction ClickAddServer(MainMenuHit hit)
    {
        switch (hit.Kind)
        {
            case MainMenuHitKind.ServerNameField: View.Focus = MenuField.ServerName; return MainMenuAction.None;
            case MainMenuHitKind.AddressField: View.Focus = MenuField.Address; return MainMenuAction.None;
            case MainMenuHitKind.Confirm:
                if (!ServerAddress.TryParse(View.Address.Text, out var address))
                {
                    View.Notice = BadAddressNotice;
                    return MainMenuAction.None;
                }
                if (Guarded(() => serverStore.Add(View.ServerName.Text, address.Host, address.Port)))
                    OpenMultiplayer();
                else
                    SetErrorNotice();
                return MainMenuAction.None;
            case MainMenuHitKind.Back:
                OpenMultiplayer();
                return MainMenuAction.None;
            default:
                return MainMenuAction.None;
        }
    }

    private MainMenuAction ClickDirectConnect(MainMenuHit hit)
    {
        switch (hit.Kind)
        {
            case MainMenuHitKind.AddressField: View.Focus = MenuField.Address; return MainMenuAction.None;
            case MainMenuHitKind.Connect:
                if (!ServerAddress.TryParse(View.Address.Text, out var address))
                {
                    View.Notice = BadAddressNotice;
                    return MainMenuAction.None;
                }
                ConnectRequest = new ConnectTarget(address.Host, address);
                return MainMenuAction.Connect;
            case MainMenuHitKind.Back:
                OpenMultiplayer();
                return MainMenuAction.None;
            default:
                return MainMenuAction.None;
        }
    }

    private MainMenuAction ClickHost(MainMenuHit hit)
    {
        switch (hit.Kind)
        {
            case MainMenuHitKind.HostWorld when IsRow(hit.Row):
            {
                if (View.WorldsLocked)
                    return MainMenuAction.None;
                var world = View.Worlds[hit.Row];
                return RefusesOldFormat(world) ? MainMenuAction.None : RequestHost(world);
            }

            case MainMenuHitKind.PortField: View.Focus = MenuField.Port; return MainMenuAction.None;
            case MainMenuHitKind.ToggleUpnp: View.Upnp = !View.Upnp; return MainMenuAction.None;
            case MainMenuHitKind.Create: OpenCreate(thenHost: true); return MainMenuAction.None;
            case MainMenuHitKind.Back: OpenMultiplayer(); return MainMenuAction.None;
            default: return MainMenuAction.None;
        }
    }

    // A host request for the world with the page's port and UPnP choice, or the bad-port notice with the page kept up
    // to fix it (a busy port comes back the same way once Program tries it).
    private MainMenuAction RequestHost(WorldInfo world)
    {
        if (!TryParsePort(View.Port.Text, out var port))
        {
            View.Notice = BadPortNotice;
            return MainMenuAction.None;
        }
        HostRequest = new HostTarget(world, port, View.Upnp);
        return MainMenuAction.Host;
    }

    /// <summary>The host page's port: blank is <see cref="ServerAddress.DefaultPort"/>, otherwise a number in the
    /// port range.</summary>
    public static bool TryParsePort(string text, out int port)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            port = ServerAddress.DefaultPort;
            return true;
        }
        return int.TryParse(text, out port) && port is >= ServerAddress.MinPort and <= ServerAddress.MaxPort;
    }

    // A world written by another save format is refused where a world is opened (PLAY) or hosted, rather than
    // half-loaded; EDIT and DELETE stay available so the player can wipe it.
    private bool RefusesOldFormat(WorldInfo world)
    {
        if (world.Version == WorldStore.CurrentVersion)
            return false;
        ShowNotice($"'{world.Name}' was saved by an older version (format {world.Version}, " +
                   $"this build reads {WorldStore.CurrentVersion}); delete it to make room.");
        return true;
    }

    // The new-world form, from the worlds page (CREATE plays the world) or the host page (CREATE hosts it)
    private void OpenCreate(bool thenHost)
    {
        if (View.WorldsLocked)
            return;
        View.Name.Clear();
        View.Seed.Clear();
        View.Focus = MenuField.Name;
        View.CreateThenHost = thenHost;
        View.Page = MainMenuPage.Create;
    }

    // Back from the new-world form to the page that opened it
    private void LeaveCreate()
    {
        if (View.CreateThenHost)
            OpenHost();
        else
            OpenWorlds();
    }

    private bool IsRow(int row) => row >= 0 && row < View.Worlds.Count;

    private bool IsServerRow(int row) => row >= 0 && row < View.Servers.Count;

    private MainMenuAction Confirm()
    {
        if (View.Page == MainMenuPage.Create)
        {
            // A bad port is caught before the world is made, so no stray world is left behind
            if (View.CreateThenHost && !TryParsePort(View.Port.Text, out _))
            {
                OpenHost();
                View.Notice = BadPortNotice;
                return MainMenuAction.None;
            }
            WorldInfo? created = null;
            if (Guarded(() => created = store.Create(View.Name.Text, WorldSeedParser.Parse(View.Seed.Text, randomSeed))))
            {
                if (View.CreateThenHost)
                    return RequestHost(created!);
                PlayRequest = created;
                PlayRequestIsNew = true;
                return MainMenuAction.Play;
            }
            // The notice lives on the list page, so a failed create lands there with it.
            if (View.CreateThenHost)
            {
                OpenHost();
                SetErrorNotice();
            }
            else
                OpenWorldsWithError();
            return MainMenuAction.None;
        }

        if (View.Page == MainMenuPage.Edit && View.SelectedWorld is { } world)
        {
            if (Guarded(() => store.Rename(world, View.Name.Text)))
                OpenWorlds();
            else
                OpenWorldsWithError();
        }
        return MainMenuAction.None;
    }

    private void ConfirmDelete()
    {
        if (View.SelectedWorld is not { } world)
        {
            CancelDelete();
            return;
        }
        if (Guarded(() => store.Delete(world)))
            OpenWorlds();
        else
            OpenWorldsWithError();
    }

    private void CancelDelete()
    {
        View.SelectedRow = -1;
        View.Page = MainMenuPage.Worlds;
    }

    private void ConfirmDeleteServer()
    {
        if (View.SelectedServer is not { } server)
        {
            CancelDeleteServer();
            return;
        }
        if (Guarded(() => serverStore.Delete(server)))
            OpenMultiplayer();
        else
        {
            OpenMultiplayer();
            SetErrorNotice();
        }
    }

    private void CancelDeleteServer()
    {
        View.SelectedServerRow = -1;
        View.Page = MainMenuPage.Multiplayer;
    }

    // A page's notice is about that page (a lost connection on the server list, a bad address on
    // the form), so opening a form drops it; the worlds lock is the one notice that outlives pages.
    private void ClearPageNotice()
    {
        if (!View.WorldsLocked)
            View.Notice = null;
    }

    // OpenWorlds clears the notice, so a failure re-raises it after the list is re-read.
    private void OpenWorldsWithError()
    {
        OpenWorlds();
        SetErrorNotice();
    }

    private void SetErrorNotice()
    {
        if (!View.WorldsLocked)
            View.Notice = StoreErrorNotice;
    }

    private bool Guarded(Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Worlds: {e.Message}");
            SetErrorNotice();
            return false;
        }
    }
}
