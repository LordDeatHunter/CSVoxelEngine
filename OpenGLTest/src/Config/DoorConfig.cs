namespace VoxelEngine.Config;

/// <summary>Tuning of the side-hinged doors' swing (<c>DoorSwings</c>).</summary>
public static class DoorConfig
{
    /// <summary>How long a door takes to swing fully open or shut, in seconds.</summary>
    public const float SwingSeconds = 0.32f;

    /// <summary>The farthest a swinging panel moves a body it touches in one tick, in blocks: above what the
    /// panel's tip travels in a slow frame, and small enough that no shove ever carries a body through a wall.</summary>
    public const float MaxPushBlocks = 0.3f;
}
