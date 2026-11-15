using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.Effects.Lighting;

/// <summary>
/// One flickering dynamic point light: the cell it sits in, its linear color and its strength relative to a
/// torch (emission 14 = 1). A torch, campfire, kiln or lantern is <see cref="Warm"/>; a held block takes its
/// own <c>light.color</c> and <c>light.emission</c> (<see cref="OfBlock"/>).
/// </summary>
public readonly record struct FireLight(Vector3 Cell, Vector3 Color, float Strength)
{
    private const float TorchEmission = 14f;

    /// <summary>The default campfire-orange light at full strength.</summary>
    internal static FireLight Warm(Vector3 cell) => new(cell, FrameLighting.CampfireLightColor, 1f);

    /// <summary>
    /// The light a block of this id gives off in a hand: its palette color (slot 0 is the warm default) scaled by
    /// its emission relative to a torch.
    /// </summary>
    internal static FireLight OfBlock(Vector3 cell, ushort blockId)
    {
        var block = BlockType.ById[blockId];
        if (block == null || block.LightEmission == 0) return Warm(cell);
        var color = block.LightColorSlot == 0 ? FrameLighting.CampfireLightColor : BlockLightPalette.Linear[block.LightColorSlot];
        return new FireLight(cell, color, block.LightEmission / TorchEmission);
    }
}
