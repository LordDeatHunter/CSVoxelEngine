namespace VoxelEngine.Content.Blocks;

/// <summary>
/// The pose the player holds while a timed use charges (<c>use.seconds</c>), from the <c>use.pose</c>
/// field of <c>assets/data/items/&lt;name&gt;.json</c>.
/// </summary>
public enum UsePose
{
    /// <summary>The hand raised in front of the chest presenting the item (a bandage, a firestarter); the default.</summary>
    Raise,

    /// <summary>
    /// A bow's two-handed draw: the item arm held out forward, the other arm pulling back to the cheek
    /// as the charge rises, the shoulders turning side-on. It holds at full draw until released (the
    /// use never runs by itself), and letting go snaps the arms back.
    /// </summary>
    Bow,
}
