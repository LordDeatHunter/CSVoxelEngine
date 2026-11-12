namespace VoxelEngine.UI.Waystone;

/// <summary>What a point on the waystone screen lands on (<see cref="WaystoneLayout.HitAt"/>).</summary>
public enum WaystoneHitKind
{
    /// <summary>Outside the page's box or panel.</summary>
    None,

    /// <summary>Inside it, but on no button, field or row.</summary>
    Panel,

    /// <summary>CONFIRM on the discovery popup.</summary>
    Confirm,

    /// <summary>RENAME on the discovery popup.</summary>
    Rename,

    /// <summary>The rename dialog's text field.</summary>
    RenameField,

    /// <summary>RENAME (save) in the rename dialog.</summary>
    RenameOk,

    /// <summary>CANCEL in the rename dialog.</summary>
    RenameCancel,

    /// <summary>The teleport list's search field.</summary>
    Search,

    /// <summary>A row of the teleport list (<see cref="WaystoneHit.Row"/>).</summary>
    Row,
}
