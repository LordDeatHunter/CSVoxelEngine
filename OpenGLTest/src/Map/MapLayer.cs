using System;

namespace VoxelEngine.Map;

/// <summary>What a map layer looks at: the overworld's surface, the sky islands, or a slice under a roof.</summary>
public enum MapLayerKind : byte
{
    Surface,
    Sky,
    Cave,
}

/// <summary>
/// The vertical window the map scans each column through: from <see cref="TopY"/> down to <see cref="BottomY"/>
/// (inclusive), keeping the first map-visible cell. The surface and sky layers are fixed windows (their tiles are the
/// session's explored map); a cave slice follows the eye in <see cref="MapConfig.CaveSliceStep"/> steps. GL-free.
/// </summary>
public readonly record struct MapLayer(MapLayerKind Kind, int TopY, int BottomY)
{
    /// <summary>The overworld from just under the solid cloud slab (which sits above the relief ceiling, so no
    /// terrain is cut) down to the barrier: what open ground shows.</summary>
    public static MapLayer Surface { get; } =
        new(MapLayerKind.Surface, WorldGenerationConfig.CloudSlabBottomY - 1, WorldGenerationConfig.HellTopY + 1);

    /// <summary>The sky islands' band, down to the top of the cloud slab: what a player up among the islands sees.</summary>
    public static MapLayer Sky { get; } =
        new(MapLayerKind.Sky, WorldGenerationConfig.SkyIslandTopY, WorldGenerationConfig.CloudSlabTopY + 1);

    /// <summary>The cave slice for an eye at <paramref name="eyeY"/>: its top a little above the head, snapped up to
    /// the slice step (so it never drops under the eye), looking <see cref="MapConfig.CaveScanDepth"/> cells down.</summary>
    public static MapLayer CaveAt(float eyeY)
    {
        var top = (int)MathF.Floor(eyeY) + MapConfig.CaveSliceHeadroom;
        top += MathUtils.Mod(-top, MapConfig.CaveSliceStep);
        return new MapLayer(MapLayerKind.Cave, top, top - MapConfig.CaveScanDepth);
    }

    /// <summary>
    /// The layer for an eye at <paramref name="eyeY"/>: hell is all caverns, so it is always a slice; up among the
    /// sky islands it is the sky band; anywhere else a roof over the head (<paramref name="roofed"/>) means a slice
    /// and open ground means the surface.
    /// </summary>
    public static MapLayer For(float eyeY, bool roofed)
    {
        if (eyeY <= WorldGenerationConfig.HellTopY + 1) return CaveAt(eyeY);
        if (eyeY >= WorldGenerationConfig.SkyIslandBottomY - MapConfig.SkyLayerMargin) return Sky;
        return roofed ? CaveAt(eyeY) : Surface;
    }
}
