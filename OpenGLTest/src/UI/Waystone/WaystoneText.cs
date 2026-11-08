using System.Globalization;
using VoxelEngine.Simulation.Waystones;

namespace VoxelEngine.UI.Waystone;

/// <summary>The waystone screen's strings, GL-free: the fixed labels, the mana footer rule and a toast per refusal.</summary>
public static class WaystoneText
{
    public const string DiscoveredLine = "has been discovered";
    public const string RenameTitle = "Rename waystone";
    public const string RenameHint = "Enter saves, Esc cancels";
    public const string SearchPlaceholder = "Search waystones";
    public const string NoneFound = "No waystones found yet";
    public const string NothingMatches = "Nothing matches";
    public const string Here = "here";
    public const string Lost = "lost";
    public const string TripRefused = "The waystone refused the trip";

    /// <summary>The footer under the list: the trip's price and whether the player can pay it now, and so whether it
    /// reads as met (<c>Met</c>, drawn green) or not (red).</summary>
    public static (string Text, bool Met) ManaLine(bool free, bool moreThanHalf)
    {
        if (free) return ("Free while creative or flying", true);
        return moreThanHalf
            ? ("Travel costs all your mana", true)
            : ("Needs more than half your mana", false);
    }

    /// <summary>A row's distance column: "here" for the waystone the player stands at, else the blocks away.</summary>
    public static string Distance(in WaystoneRow row) =>
        row.Current ? Here : string.Create(CultureInfo.InvariantCulture, $"{row.Distance} blocks");

    /// <summary>The toast for a refused trip (empty for an allowed one).</summary>
    public static string Refusal(WaystoneTeleportVerdict verdict) => verdict switch
    {
        WaystoneTeleportVerdict.NotAtWaystone => "Stand at a waystone to travel",
        WaystoneTeleportVerdict.Current => "You are already here",
        WaystoneTeleportVerdict.NotDiscovered => "You have not found that waystone",
        WaystoneTeleportVerdict.Unknown or WaystoneTeleportVerdict.Lost => "That waystone is lost",
        WaystoneTeleportVerdict.NeedsMana => "Needs more than half your mana",
        _ => "",
    };
}
