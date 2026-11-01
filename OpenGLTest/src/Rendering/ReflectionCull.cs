using System.Numerics;

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

    /// <summary>
    /// The one definition of the mirror's pose: the eye reflected across the horizontal plane at
    /// <paramref name="planeY"/>, looking along the reflected view direction, with world up kept as
    /// up (pitch stays within ±89°, so it never parallels the view). Returns the mirror's view
    /// matrix; <c>ReflectionPass.RenderReflection</c> draws through it and the shadow caster cull
    /// bounds the mirror's receivers with it.
    /// </summary>
    internal static Matrix4x4 MirrorView(Vector3 eye, Vector3 viewDir, float planeY,
        out Vector3 mirroredEye, out Vector3 mirroredForward)
    {
        mirroredEye = new Vector3(eye.X, 2f * planeY - eye.Y, eye.Z);
        mirroredForward = new Vector3(viewDir.X, -viewDir.Y, viewDir.Z);
        return Matrix4x4.CreateLookAt(mirroredEye, mirroredEye + mirroredForward, Vector3.UnitY);
    }

    /// <summary>
    /// Whether the mirror could render this frame, decided conservatively before the shadow pass.
    /// <c>ReflectionPass.RenderReflection</c>'s own gate also needs <c>TerrainPasses.WaterVisible</c>,
    /// <c>ReflectionReuse.ShouldRefresh</c> and a live reflection target, but all three are decided
    /// after <c>ShadowPass.RenderShadows</c>, so this keeps only the parts known by then: reflections
    /// on and <see cref="ReflectionReuse.MirrorApplies"/>. A reused mirror samples no shadow map of
    /// the current frame, so true here only ever over-states the mirror's receivers. Any relaxation
    /// of <c>RenderReflection</c>'s gate must be followed here, or the mirror would sample shadow
    /// texels the caster cull dropped.
    /// </summary>
    internal static bool MirrorMayRender(bool reflectionsOn, float eyeY, bool eyeInWater,
        float planeY = RenderManager.WaterPlaneY) =>
        reflectionsOn && ReflectionReuse.MirrorApplies(eyeY, planeY, eyeInWater);
}
