namespace VoxelEngine.UI.Hud;

/// <summary>
/// What the crosshair draws with this frame, GL-free: a throw's charge (the arms spread), a mined
/// block's break progress (the ring fills; zero in creative and with no target) and the hit
/// marker's alpha (the X flash when a hit lands).
/// </summary>
public readonly record struct CrosshairState(float ThrowCharge, float BreakProgress, float HitMarker);
