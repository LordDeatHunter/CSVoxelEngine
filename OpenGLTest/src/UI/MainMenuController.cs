using System;
using System.IO;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Every rule of the main menu in one GL-free place: page transitions, the four non-character
/// keys, typing into the focused field and every <see cref="WorldStore"/> call (list, create,
/// rename, delete). Program cannot be tested, so the whole flow is pinned here and Program only
/// feeds it hits and keys and acts on the <see cref="MainMenuAction"/> it gets back (a
/// <see cref="MainMenuAction.Play"/> carries its world in <see cref="PlayRequest"/>).
/// <para>
/// It never touches GL, the cursor or Silk. The settings page is only opened and closed here;
/// Program owns that page's own clicks and slider drags through <see cref="SettingsLayout"/>,
/// shared with the pause menu. Store errors never escape: they are logged in full and shown as
/// the one-line <see cref="StoreErrorNotice"/> on the worlds page. Main thread only.
/// </para>
/// </summary>
public sealed class MainMenuController(WorldStore store, Func<int> randomSeed)
{
    /// <summary>The notice shown after a failed store call (well under <see cref="BitmapFont.MaxLayoutChars"/>).</summary>
    public const string StoreErrorNotice = "Saves folder error - see the console";

    public MainMenuView View { get; } = new();

    /// <summary>The world PLAY or CREATE asked Program to open, until <see cref="TakePlayRequest"/>.</summary>
    public WorldInfo? PlayRequest { get; private set; }

    /// <summary>Whether the latest request came from CREATE (a fresh world); readable until the next request.</summary>
    public bool PlayRequestIsNew { get; private set; }

    /// <summary>Returns the pending play request and clears it.</summary>
    public WorldInfo? TakePlayRequest()
    {
        var request = PlayRequest;
        PlayRequest = null;
        return request;
    }

    public void OpenMain()
    {
        View.Page = MainMenuPage.Main;
        View.SelectedRow = -1;
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

    /// <summary>Shows a one-line notice under the worlds list until the next <see cref="OpenWorlds"/>.</summary>
    public void ShowNotice(string? notice) => View.Notice = notice;

    public MainMenuAction Click(MainMenuHit hit)
    {
        switch (View.Page)
        {
            case MainMenuPage.Main:
                switch (hit.Kind)
                {
                    case MainMenuHitKind.Start: OpenWorlds(); break;
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
                return MainMenuAction.None;

            case MenuKey.Enter:
                // Enter never confirms a delete: that popup needs its own click.
                return View.Page is MainMenuPage.Create or MainMenuPage.Edit ? Confirm() : MainMenuAction.None;

            case MenuKey.Escape:
                switch (View.Page)
                {
                    case MainMenuPage.ConfirmDelete: CancelDelete(); break;
                    case MainMenuPage.Create:
                    case MainMenuPage.Edit: OpenWorlds(); break; // unsaved edits are discarded
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

    /// <summary>Scrolls the worlds list by <paramref name="rows"/> rows, clamped to the list; other pages ignore it.</summary>
    public void Scroll(float rows, Vector2 screenSize)
    {
        if (View.Page != MainMenuPage.Worlds)
            return;
        View.ScrollOffset = MainMenuLayout.ClampedOffset(
            View.ScrollOffset + rows * MainMenuLayout.RowHeight, View.Worlds.Count, screenSize);
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

    private bool IsRow(int row) => row >= 0 && row < View.Worlds.Count;

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
