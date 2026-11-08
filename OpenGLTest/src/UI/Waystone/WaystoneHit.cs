namespace VoxelEngine.UI.Waystone;

/// <summary>What a point on the waystone screen lands on: the kind, and for a list row its index (else -1).</summary>
public readonly record struct WaystoneHit(WaystoneHitKind Kind, int Row = -1);
