using VoxelEngine.Player;

namespace VoxelEngine.Interaction;

/// <summary>What claimed a right-click (<see cref="World.TryUseSelected(float, PlayerContext, out RightClickOutcome, bool)"/>).</summary>
public enum RightClickOutcome
{
    /// <summary>Nothing claimed the click.</summary>
    None,
    /// <summary>A campfire, quern, kiln or cast mold took it.</summary>
    Station,
    /// <summary>The held item's own use took effect (a bucket, a firestarter, a pile, a converter).</summary>
    ItemUse,
    /// <summary>The selected block was placed.</summary>
    Place,
}
