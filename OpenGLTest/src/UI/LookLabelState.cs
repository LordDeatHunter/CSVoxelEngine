using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// What the HUD's look-at label draws with this frame, GL-free: the rule's text and flags
/// (<see cref="LookLabel.State"/>), the targeted cell and its block's state byte, and the voxel
/// light there (for the debug lines the profiler overlay adds). Built by the world from the
/// selection raycast; null with no target.
/// </summary>
public readonly record struct LookLabelState(
    string Text,
    bool Known,
    bool Mineable,
    Vector3 Cell,
    byte BlockId,
    byte BlockState,
    EntityLightSample Light);
