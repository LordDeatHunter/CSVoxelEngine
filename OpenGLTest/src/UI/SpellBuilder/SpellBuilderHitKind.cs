namespace VoxelEngine.UI.SpellBuilder;

/// <summary>What a point on the spell builder panel lands on (<see cref="SpellBuilderLayout.HitAt"/>).</summary>
public enum SpellBuilderHitKind
{
    /// <summary>Outside the panel.</summary>
    None,

    /// <summary>Inside the panel, but on no button or row.</summary>
    Panel,

    /// <summary>A part row of one of the four kind columns.</summary>
    Part,

    /// <summary>One of the held staff's slot buttons.</summary>
    Slot,

    /// <summary>The BIND button.</summary>
    Bind,

    /// <summary>The CLEAR button.</summary>
    Clear,
}
