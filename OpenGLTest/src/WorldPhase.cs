namespace silkgltriangle;

/// <summary>Where a <see cref="World"/> is in its lifecycle.</summary>
public enum WorldPhase
{
    /// <summary>
    /// Streaming chunks and far LOD around the (restored) player, with no input, player physics,
    /// health, mobs, drops, fluid tick or autosave, until <see cref="WorldLoadGate"/> reports the
    /// ready sphere meshed or its timeout passes. Only a world built with a save store starts here.
    /// </summary>
    Loading,

    /// <summary>The ordinary game: every system runs each <see cref="World.Update"/>.</summary>
    Playing,

    /// <summary>
    /// <see cref="World.SaveAndDispose"/> has run: the workers are stopped, the save is flushed and
    /// the world's geometry is released. <see cref="World.Update"/> is a no-op and
    /// <see cref="World.Render"/> must not be called.
    /// </summary>
    Disposed
}
