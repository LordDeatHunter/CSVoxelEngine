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
    /// <summary>A chest took it: a local player opens the chest panel on it (<see cref="RightClickAction.LastContainerCell"/>).</summary>
    Container,
    /// <summary>A waystone took it: a local player discovers it and opens the waystone screen (<see cref="RightClickAction.LastWaystoneCell"/>).</summary>
    Waystone,
    /// <summary>A smithing hammer on an anvil holding a metal stack took it: a local player opens the anvil's recipe list
    /// (<see cref="RightClickAction.LastForgeCell"/>).</summary>
    Forge,
    /// <summary>A battle tower's roof altar took it: the authoritative side begins the ritual, a client only judges it;
    /// the verdict is in <see cref="RightClickAction.LastAltarVerdict"/>.</summary>
    Altar,
    /// <summary>The held item's timed use began charging (<see cref="RightClickAction.TryBeginUseCharge()"/>); nothing else claimed the click.</summary>
    UseCharge,
    /// <summary>A held or placed lantern opens its fuel list.</summary>
    Lantern,
    /// <summary>Crouch-right-click picks up a placed lantern, if the inventory has room.</summary>
    LanternPickup,
}
