using System.Collections.Generic;
using VoxelEngine.Networking;
using VoxelEngine.Rendering;
using VoxelEngine.Saves;

namespace VoxelEngine.UI.Menus;

/// <summary>Which main-menu page is showing (the menu the game boots to, with no world loaded).</summary>
public enum MainMenuPage
{
    Main,
    Worlds,
    Create,
    Edit,
    ConfirmDelete,
    Settings,
    Multiplayer,
    AddServer,
    DirectConnect,
    ConfirmDeleteServer,
}

/// <summary>What a click on the main menu landed on.</summary>
public enum MainMenuHitKind
{
    None,
    Start,
    Multiplayer,
    Settings,
    Exit,
    Back,
    Create,
    Play,
    Edit,
    Delete,
    ConfirmDelete,
    CancelDelete,
    NameField,
    SeedField,
    Confirm,
    AddServer,
    DirectConnect,
    Connect,
    DeleteServer,
    ServerNameField,
    AddressField,
    ConfirmDeleteServer,
    CancelDeleteServer,
}

/// <summary>
/// A main-menu hit: its kind plus, for the per-world PLAY / EDIT / DELETE buttons and the per-server
/// CONNECT / DELETE buttons, the row of <see cref="MainMenuView.Worlds"/> or
/// <see cref="MainMenuView.Servers"/> it belongs to (-1 otherwise).
/// </summary>
public readonly record struct MainMenuHit(MainMenuHitKind Kind, int Row = -1);

/// <summary>Which text field has keyboard focus on the create / edit / add-server pages.</summary>
public enum MenuField
{
    None,
    Name,
    Seed,
    ServerName,
    Address,
}

/// <summary>
/// The four non-character keys the main menu reacts to, Silk-free so
/// <see cref="MainMenuController"/> can be driven by tests.
/// </summary>
public enum MenuKey
{
    Backspace,
    Tab,
    Enter,
    Escape,
}

/// <summary>What Program has to do after a main-menu click or key.</summary>
public enum MainMenuAction
{
    None,
    Play,
    Exit,
    Connect,
}

/// <summary>A server the menu asked Program to connect to, until <see cref="MainMenuController.TakeConnectRequest"/>.</summary>
public readonly record struct ConnectTarget(string Name, ServerAddress Address);

/// <summary>
/// Selection and text state of the main menu. Owned by Program's <see cref="MainMenuController"/>;
/// <see cref="UIRenderer"/> only reads it.
/// </summary>
public sealed class MainMenuView
{
    /// <summary>Most characters the seed field accepts (well under <see cref="BitmapFont.MaxLayoutChars"/>).</summary>
    public const int SeedMaxLength = 32;

    /// <summary>Most characters the server address field accepts.</summary>
    public const int AddressMaxLength = 64;

    public MainMenuPage Page { get; set; } = MainMenuPage.Main;

    /// <summary>The worlds page's rows, as <see cref="WorldStore.List"/> returned them.</summary>
    public List<WorldInfo> Worlds { get; } = [];

    /// <summary>
    /// Raw scroll of the worlds list in canvas units; draw and hit-test clamp it on read with
    /// that frame's row count (<see cref="MainMenuLayout.ClampedOffset"/>), as the inventory does.
    /// </summary>
    public float ScrollOffset { get; set; }

    /// <summary>The row an edit or delete acts on, -1 for none.</summary>
    public int SelectedRow { get; set; } = -1;

    public WorldInfo? SelectedWorld =>
        SelectedRow >= 0 && SelectedRow < Worlds.Count ? Worlds[SelectedRow] : null;

    /// <summary>The multiplayer page's rows, as <see cref="ServerStore.List"/> returned them.</summary>
    public List<ServerInfo> Servers { get; } = [];

    /// <summary>Raw scroll of the server list in canvas units, clamped like the worlds list.</summary>
    public float ServerScrollOffset { get; set; }

    /// <summary>The server row a delete acts on, -1 for none.</summary>
    public int SelectedServerRow { get; set; } = -1;

    public ServerInfo? SelectedServer =>
        SelectedServerRow >= 0 && SelectedServerRow < Servers.Count ? Servers[SelectedServerRow] : null;

    public TextFieldState Name { get; } = new(WorldNames.MaxNameLength);

    public TextFieldState Seed { get; } = new(SeedMaxLength);

    /// <summary>The add-server page's display name field.</summary>
    public TextFieldState ServerName { get; } = new(WorldNames.MaxNameLength);

    /// <summary>The add-server and direct-connect page's host[:port] field.</summary>
    public TextFieldState Address { get; } = new(AddressMaxLength);

    public MenuField Focus { get; set; }

    public TextFieldState? FocusedField => Focus switch
    {
        MenuField.Name => Name,
        MenuField.Seed => Seed,
        MenuField.ServerName => ServerName,
        MenuField.Address => Address,
        _ => null,
    };

    /// <summary>
    /// One line shown under a list (a store error, the seed-lock message, a bad address); callers
    /// keep it under <see cref="BitmapFont.MaxLayoutChars"/> so it never needs wrapping.
    /// </summary>
    public string? Notice { get; set; }

    /// <summary>True once PLAY and CREATE are refused for the rest of the process (seed locked).</summary>
    public bool WorldsLocked { get; set; }
}
