namespace VoxelEngine.UI.Hud;

/// <summary>What the boss bar draws for one frame: the boss's display name and its remaining health share (0..1).</summary>
public readonly record struct BossBarState(string Name, float Share);
