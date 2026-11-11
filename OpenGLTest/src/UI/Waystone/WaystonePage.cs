namespace VoxelEngine.UI.Waystone;

/// <summary>Which page of the waystone screen is up (<see cref="WaystoneView.Page"/>).</summary>
public enum WaystonePage
{
    /// <summary>Nothing is up.</summary>
    Closed,

    /// <summary>The discovery popup: "{name} has been discovered", CONFIRM and (first discoverer only) RENAME.</summary>
    Discovered,

    /// <summary>The rename dialog: a text field, RENAME and CANCEL.</summary>
    Rename,

    /// <summary>The teleport list: the player's discovered waystones, a search field and the mana requirement.</summary>
    List,
}
