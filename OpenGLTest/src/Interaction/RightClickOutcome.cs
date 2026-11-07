using VoxelEngine.Player;

namespace VoxelEngine.Interaction;

/// <summary>What claimed a right-click (<see cref="World.TryUseSelected(float, PlayerContext, out RightClickOutcome, bool)"/>).</summary>
public enum RightClickOutcome
{
    /// <summary>Nothing claimed the click.</summary>
    None,
    /// <summary>A campfire, quern, kiln or cast mold took it, or an openable block (a door or trapdoor) flipped.</summary>
    Station,
    /// <summary>The held item's own use took effect (a bucket, a firestarter, a pile, a converter).</summary>
    ItemUse,
    /// <summary>The selected block was placed.</summary>
    Place,
    /// <summary>The held staff cast its selected slot.</summary>
    Cast,
    /// <summary>A spell table took it: a local player opens the spell builder (client-side only).</summary>
    SpellTable,
}
