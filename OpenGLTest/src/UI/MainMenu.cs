using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>Which main-menu page is showing (the menu the game boots to, with no world loaded).</summary>
public enum MainMenuPage
{
    Main,
    Worlds,
    Create,
    Edit,
    ConfirmDelete,
    Settings,
}

/// <summary>What a click on the main menu landed on.</summary>
public enum MainMenuHitKind
{
    None,
    Start,
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
}

/// <summary>
/// A main-menu hit: its kind plus, for the per-world PLAY / EDIT / DELETE buttons, the row of
/// <see cref="MainMenuView.Worlds"/> it belongs to (-1 otherwise).
/// </summary>
public readonly record struct MainMenuHit(MainMenuHitKind Kind, int Row = -1);

/// <summary>Which text field has keyboard focus on the create / edit pages.</summary>
public enum MenuField
{
    None,
    Name,
    Seed,
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
}

/// <summary>
/// Selection and text state of the main menu. Owned by Program's <see cref="MainMenuController"/>;
/// <see cref="UIRenderer"/> only reads it.
/// </summary>
public sealed class MainMenuView
{
    /// <summary>Most characters the seed field accepts (well under <see cref="BitmapFont.MaxLayoutChars"/>).</summary>
    public const int SeedMaxLength = 32;

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

    public TextFieldState Name { get; } = new(WorldNames.MaxNameLength);

    public TextFieldState Seed { get; } = new(SeedMaxLength);

    public MenuField Focus { get; set; }

    public TextFieldState? FocusedField => Focus switch
    {
        MenuField.Name => Name,
        MenuField.Seed => Seed,
        _ => null,
    };

    /// <summary>
    /// One line shown under the worlds list (a store error, the seed-lock message); callers keep
    /// it under <see cref="BitmapFont.MaxLayoutChars"/> so it never needs wrapping.
    /// </summary>
    public string? Notice { get; set; }

    /// <summary>True once PLAY and CREATE are refused for the rest of the process (seed locked).</summary>
    public bool WorldsLocked { get; set; }
}
