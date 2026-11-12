using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.Waystone;
using Xunit;

namespace VoxelEngine.Tests.UI.Waystone;

/// <summary>
/// The waystone screen's page rules (CONFIRM leads to the list, RENAME only for the first discoverer, Escape steps back
/// one level) and its rows (the current waystone first, lost ones last, nearest first, filtered by name, named by the
/// registry when it has a row).
/// </summary>
public sealed class WaystoneViewTests
{
    private static readonly (int X, int Y, int Z) Here = (0, 10, 0);

    [Fact]
    public void ConfirmLeadsFromTheDiscoveryPopupToTheList()
    {
        var view = new WaystoneView();
        view.ShowDiscovery(Here, "Kafuziro", canRename: false);
        Assert.Equal(WaystonePage.Discovered, view.Page);
        Assert.False(view.HasFocusedField);

        view.Confirm();

        Assert.Equal(WaystonePage.List, view.Page);
        Assert.True(view.IsOpen);
    }

    [Fact]
    public void OnlyTheFirstDiscovererMayRenameAndTheDialogStartsFromTheCurrentName()
    {
        var view = new WaystoneView();
        view.ShowDiscovery(Here, "Kafuziro", canRename: false);
        Assert.False(view.BeginRename());
        Assert.Equal(WaystonePage.Discovered, view.Page);

        view.ShowDiscovery(Here, "Kafuziro", canRename: true);
        Assert.True(view.BeginRename());
        Assert.Equal(WaystonePage.Rename, view.Page);
        Assert.True(view.HasFocusedField);
        Assert.Equal("Kafuziro", view.RenameField.Text);

        // Typing replaces the selected name; a blank name cannot be saved
        view.RenameField.TryAppend(' ');
        Assert.False(view.CanSubmitRename);
        view.RenameField.TryAppend('H');
        Assert.True(view.CanSubmitRename);

        view.FinishRename("Home");
        Assert.Equal(WaystonePage.Discovered, view.Page);
        Assert.Equal("Home", view.Name);
    }

    [Fact]
    public void EscapeStepsBackOneLevel()
    {
        var view = new WaystoneView();
        view.ShowDiscovery(Here, "Kafuziro", canRename: true);
        view.BeginRename();
        view.RenameField.TryAppend('X');

        view.Back();
        Assert.Equal(WaystonePage.Discovered, view.Page);
        Assert.Equal("Kafuziro", view.Name);

        view.Confirm();
        view.SearchFocused = true;
        view.Back();
        Assert.Equal(WaystonePage.List, view.Page);
        Assert.False(view.SearchFocused);

        view.Back();
        Assert.False(view.IsOpen);
    }

    [Fact]
    public void TheListPutsTheCurrentWaystoneFirstTheLostOnesLastAndTheRestNearestFirst()
    {
        var known = new DiscoveredWaystones();
        known.Add(Here.X, Here.Y, Here.Z, "Home");
        known.Add(300, 10, 0, "Far");
        known.Add(20, 10, 0, "Near");
        known.Add(5, 10, 0, "Broken");
        var records = new Dictionary<(int, int, int), WaystoneRecord>
        {
            [(5, 10, 0)] = new("Broken", "@local", lost: true),
            [(300, 10, 0)] = new("Renamed Far", "@local"),
        };
        var view = new WaystoneView();
        view.ShowList(Here, "Home", canRename: true);

        var rows = view.Rows(known, 1, (x, y, z) => records.GetValueOrDefault((x, y, z)), (_, _, _) => WaystoneRealm.Surface);

        Assert.Equal(["Home", "Near", "Renamed Far", "Broken"], rows.Select(r => r.Name));
        Assert.True(rows[0].Current);
        Assert.False(rows[0].Selectable);
        Assert.False(rows[3].Selectable);
        Assert.True(rows[1].Selectable);
        Assert.True(rows[1].Distance < rows[2].Distance);
    }

    [Fact]
    public void TheSearchFiltersByNameIgnoringCaseAndTheRowsFollowEveryChange()
    {
        var known = new DiscoveredWaystones();
        known.Add(Here.X, Here.Y, Here.Z, "Home");
        known.Add(20, 10, 0, "Kafuziro");
        var view = new WaystoneView();
        view.ShowList(Here, "Home", canRename: false);
        WaystoneRecord? NoRecord(int x, int y, int z) => null;
        WaystoneRealm Surface(int x, int y, int z) => WaystoneRealm.Surface;

        foreach (var c in "FUZ") view.Search.Text.TryAppend(c);
        Assert.Equal(["Kafuziro"], view.Rows(known, 0, NoRecord, Surface).Select(r => r.Name));

        known.Add(40, 10, 0, "Fuzzy Hill");
        Assert.Equal(["Kafuziro", "Fuzzy Hill"], view.Rows(known, 0, NoRecord, Surface).Select(r => r.Name));
    }
}
