using System;
using System.IO;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Every rule of the main menu in one GL-free place: page transitions, the four non-character
/// keys, typing into the focused field and every <see cref="WorldStore"/> / <see cref="ServerStore"/>
/// call (list, create, rename, delete, add server). Program cannot be tested, so the whole flow is
/// pinned here and Program only feeds it hits and keys and acts on the <see cref="MainMenuAction"/>
/// it gets back (a <see cref="MainMenuAction.Play"/> carries its world in <see cref="PlayRequest"/>,
/// a <see cref="MainMenuAction.Connect"/> its server in <see cref="ConnectRequest"/>).
/// <para>
/// It never touches GL, the cursor or Silk. The settings page is only opened and closed here;
/// Program owns that page's own clicks and slider drags through <see cref="SettingsLayout"/>,
/// shared with the pause menu. Store errors never escape: they are logged in full and shown as
/// the one-line <see cref="StoreErrorNotice"/> on the world/server page. Main thread only.
/// </para>
/// </summary>
public sealed class MainMenuController(WorldStore store, ServerStore serverStore, Func<int> randomSeed)
{
    /// <summary>The notice shown after a failed store call (well under <see cref="BitmapFont.MaxLayoutChars"/>).</summary>
    public const string StoreErrorNotice = "Saves folder error - see the console";

    /// <summary>The notice shown when a typed server address cannot be parsed.</summary>
    public const string BadAddressNotice = "Enter an address like 192.168.1.10 or host:25566";

    public MainMenuView View { get; } = new();

    /// <summary>The world PLAY or CREATE asked Program to open, until <see cref="TakePlayRequest"/>.</summary>
    public WorldInfo? PlayRequest { get; private set; }

    /// <summary>Whether the latest request came from CREATE (a fresh world); readable until the next request.</summary>
    public bool PlayRequestIsNew { get; private set; }

    /// <summary>The server a CONNECT asked Program to open, until <see cref="TakeConnectRequest"/>.</summary>
    public ConnectTarget? ConnectRequest { get; private set; }

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

    /// <summary>Re-reads the saved servers and shows the multiplayer list from the top.</summary>
    public void OpenMultiplayer()
    {
        View.Notice = null;
        View.Servers.Clear();
        Guarded(() => View.Servers.AddRange(serverStore.List()));
        View.ServerScrollOffset = 0f;
        View.SelectedServerRow = -1;
        View.Focus = MenuField.None;
        View.Page = MainMenuPage.Multiplayer;
    }

    public void OpenSettings() => View.Page = MainMenuPage.Settings;

    /// <summary>Back from the settings page to the main page; Program clears the slider drag itself.</summary>
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
                    case MainMenuHitKind.Back: OpenWorlds(); break;
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
                    case MainMenuPage.Create:
                    case MainMenuPage.Edit: OpenWorlds(); break; // unsaved edits are discarded
                    case MainMenuPage.AddServer:
                    case MainMenuPage.DirectConnect:
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

    /// <summary>Types one character into the focused field; refused characters and an unfocused page do nothing.</summary>
    public void Type(char c) => View.FocusedField?.TryAppend(c);

    /// <summary>Scrolls the active list by <paramref name="rows"/> rows, clamped to the list; other pages ignore it.</summary>
    public void Scroll(float rows, Vector2 screenSize)
    {
        switch (View.Page)
        {
            case MainMenuPage.Worlds:
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
                // A world written by another save format is refused here, the one place a world
                // is opened from, rather than half-loaded; EDIT and DELETE stay available so the
                // player can wipe it.
                var world = View.Worlds[hit.Row];
                if (world.Version != WorldStore.CurrentVersion)
                {
                    ShowNotice($"'{world.Name}' was saved by an older version (format {world.Version}, " +
                               $"this build reads {WorldStore.CurrentVersion}); delete it to make room.");
                    return MainMenuAction.None;
                }
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
                if (View.WorldsLocked)
                    return MainMenuAction.None;
                View.Name.Clear();
                View.Seed.Clear();
                View.Focus = MenuField.Name;
                View.Page = MainMenuPage.Create;
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
                View.ServerName.Clear();
                View.Address.Clear();
                View.Focus = MenuField.ServerName;
                View.Page = MainMenuPage.AddServer;
                return MainMenuAction.None;

            case MainMenuHitKind.DirectConnect:
                View.Address.Clear();
                View.Focus = MenuField.Address;
                View.Page = MainMenuPage.DirectConnect;
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

    private bool IsRow(int row) => row >= 0 && row < View.Worlds.Count;

    private bool IsServerRow(int row) => row >= 0 && row < View.Servers.Count;

    private MainMenuAction Confirm()
    {
        if (View.Page == MainMenuPage.Create)
        {
            WorldInfo? created = null;
            if (Guarded(() => created = store.Create(View.Name.Text, WorldSeedParser.Parse(View.Seed.Text, randomSeed))))
            {
                PlayRequest = created;
                PlayRequestIsNew = true;
                return MainMenuAction.Play;
            }
            // The notice lives on the worlds page, so a failed create lands there with it.
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
