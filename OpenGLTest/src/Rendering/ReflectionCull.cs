namespace VoxelEngine.Rendering;

/// <summary>
/// The GL-free rule behind the water mirror's opaque cull: the reflection pass draws the world with
/// <c>uClipY</c> set to <see cref="RenderManager.WaterPlaneY"/>, and <c>test-fragment.glsl</c>
/// discards every fragment with <c>fWorldPos.y &lt; uClipY</c>, so a chunk or far-LOD region whose
/// highest drawn point lies below the plane draws no pixel into the mirror and can be skipped
/// outright. The test is conservative: anything that could put a fragment at or above the plane is
/// kept, so the cull is pixel-identical.
///
/// A geometry's highest drawn point is its recorded top (<c>Chunk.WorldMax.Y</c>, an LOD region's
/// <c>YMax</c>) plus <see cref="TopMarginBlocks"/>. The model overhang is guarded by a content test
/// (<c>ReflectionCullTests</c>); the procedural emitters (tree wood, rims, pebbles, branch piles,
/// snow layers, panes, fluids) are not, and must keep emitting inside their own cell.
/// </summary>
internal static class ReflectionCull
{
    /// <summary>
    /// How far a chunk's opaque vertices can reach above <c>Chunk.WorldMax.Y</c>. A multiblock anchor
    /// on the chunk's top layer (local y 15) bakes its 2-tall model one block into the chunk above;
    /// every other baked model and procedural emitter stays inside its own cell. An LOD region's
    /// <c>YMax</c> already bounds every vertex it emits, so for regions this is pure slack.
    /// </summary>
    internal const float MeshOverhangBlocks = 1f;

    /// <summary>
    /// How far the vertex shader can lift a vertex above its meshed position, derived from
    /// <c>test-vertex.glsl</c> (keep in step with it, see the shader-sync skill):
    /// the leaf-sway Y term is <c>(w1 + w2) * 0.018</c> (at most 0.036) times the sway weight (at
    /// most 1), times <c>uSwayStrength</c> (at most <c>SettingsRows.LeafSwayMax</c>, 2), times the
    /// gust (at most 1), so at most 0.072; <c>WaveHeight</c> peaks at 0.085 and
    /// <c>LavaWaveHeight</c> at 0.088, and both apply only to water-flagged vertices, which never
    /// sway; the <c>uWind</c> lean moves only X and Z.
    /// </summary>
    internal const float VertexLiftBlocks = 0.1f;

    /// <summary>The total margin above a geometry's recorded top its fragments can reach.</summary>
    internal const float TopMarginBlocks = MeshOverhangBlocks + VertexLiftBlocks;

    /// <summary>
    /// Whether geometry whose recorded top is <paramref name="topY"/> (a chunk's <c>WorldMax.Y</c>,
    /// an LOD region's <c>YMax</c>) could put a fragment at or above the mirror's clip plane, and so
    /// must be drawn into the reflection.
    /// </summary>
    internal static bool MayReachPlane(float topY, float planeY = RenderManager.WaterPlaneY) =>
        topY + TopMarginBlocks >= planeY;
}
