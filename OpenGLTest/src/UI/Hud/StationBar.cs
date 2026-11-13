namespace VoxelEngine.UI.Hud;

/// <summary>What a station bar measures, which picks its color.</summary>
public enum StationBarKind
{
    /// <summary>Fuel left in a fire, kiln or bloomery.</summary>
    Fuel,

    /// <summary>Work in progress: a melt, a smelt, a cook, a pour, a cooling cast.</summary>
    Progress,

    /// <summary>A finished piece waiting to be collected.</summary>
    Done,
}

/// <summary>One progress bar under the look-at label: its caption, how full it is (0..1) and its kind.</summary>
public readonly record struct StationBar(string Label, float Fraction, StationBarKind Kind);
