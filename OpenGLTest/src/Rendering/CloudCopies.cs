using System;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Effects.Sky;

namespace VoxelEngine.Rendering;

/// <summary>
/// The GL-free arithmetic behind the cloud layer's 3x3 sweep: where each copy of the field sits this
/// frame, and which copies survive the frustum cull.
///
/// The class is deliberately dependency-free — <c>System</c>, <c>System.Numerics</c>,
/// <see cref="WorldGenerationConfig"/> and <see cref="Frustum"/>, no GL and no Silk.NET types — for
/// the same reason <c>HalfResTarget</c> and <c>ReflectionReuse</c> are: <c>CloudRenderer</c> needs a
/// live GL context to construct, so the only way to pin this arithmetic headlessly is to keep it out
/// here. Before the extraction the offsets, the AABB and the cull were interleaved with the draw
/// calls inside one instance method and nothing about them was testable.
///
/// What is NOT covered: the draw itself — the vertex-array binding, the two sweeps and their
/// color/depth mask toggles, and the per-copy submission — which is only reachable by running the
/// game.
///
/// Two properties are worth stating because they are what make the caller correct:
///
/// <list type="bullet">
/// <item>Copies snap to field multiples (<see cref="Snap"/>) so tiles never swim under the camera,
/// and drift is a whole-copy translation wrapped modulo the field on both axes (<see cref="Drift"/>,
/// carried along the turning <see cref="Wind"/>) — the noise and the mesh both wrap at the field
/// edge in x and z, so the wrap is seamless and nothing moves per vertex.</item>
/// <item><see cref="Gather"/> packs the survivors to the front of the destination in the same order
/// the <c>ix</c>/<c>iz</c> loop visited them. The order is what makes the packed array a drop-in for
/// the old draw sequence, so a caller that submits <c>offsets[..count]</c> submits exactly what the
/// per-copy loop used to.</item>
/// </list>
/// </summary>
internal static class CloudCopies
{
    /// <summary>
    /// Copies in one sweep: the 3x3 ring around the camera's own field cell. This is the upper bound
    /// on the survivor count, so it is also the size a <see cref="Gather"/> destination must have.
    /// </summary>
    internal const int MaxCopies = 9;

    /// <summary>
    /// How far the whole layer has drifted this frame, as (x, z), each axis wrapped into [0, field).
    /// The layer is carried along the turning wind (<see cref="Wind.Offset"/> at
    /// <see cref="WorldGenerationConfig.CloudDriftBlocksPerSecond"/>), and the pattern repeats every
    /// field on both axes, so wrapping the translation is invisible. The wrap is a positive modulo
    /// computed in double, since the wind offset can be negative on either axis.
    /// </summary>
    internal static Vector2 Drift(double time, float field)
    {
        var offset = Wind.Offset(time, WorldGenerationConfig.CloudDriftBlocksPerSecond);
        return new Vector2(Wrap(offset.X, field), Wrap(offset.Y, field));
    }

    private static float Wrap(double x, float field)
    {
        var w = (float)(x - field * Math.Floor(x / field));
        // The cast can round a value a hair under the field up to the field itself.
        return w >= field ? 0f : w;
    }

    /// <summary>
    /// The single world-XZ-to-field-UV definition the cloud shadow samples its coverage map with.
    /// <c>CloudField.BuildMesh</c> places cell i at mesh-local <c>[i * CloudCellSize, (i + 1) *
    /// CloudCellSize)</c> and every copy sits at <c>snap + k * field + drift</c> on both axes, so
    /// subtracting the drift and dividing by the field lands every copy's origin on an integer UV;
    /// the texture's REPEAT wrap handles the tiling. <c>CloudShadow</c> in test-fragment.glsl
    /// mirrors this expression.
    /// </summary>
    internal static Vector2 FieldUv(float worldX, float worldZ, Vector2 drift, float field) =>
        new((worldX - drift.X) / field, (worldZ - drift.Y) / field);

    /// <summary>
    /// The field multiple at or below <paramref name="coord"/>. Floor, not truncation: at negative
    /// coordinates truncation would round toward zero and snap the grid a whole field out of place.
    /// </summary>
    internal static float Snap(float coord, float field) => MathUtils.Snap(coord, field);

    /// <summary>
    /// Fills <paramref name="offsets"/> with the world-space (x, z) origin of every copy of the 3x3
    /// that survives the frustum cull, packed to the front in visit order, and returns how many
    /// there are. Each copy is tested as the AABB spanning one field in x and z and the full stack
    /// thickness in y, based at <see cref="WorldGenerationConfig.CloudHeight"/>.
    /// </summary>
    /// <param name="frustum">Culling frustum, built from the pass's view-projection.</param>
    /// <param name="snapX">Camera x snapped to a field multiple, from <see cref="Snap"/>.</param>
    /// <param name="snapZ">Camera z snapped to a field multiple, from <see cref="Snap"/>.</param>
    /// <param name="drift">This frame's wrapped (x, z) drift, from <see cref="Drift"/>.</param>
    /// <param name="field">Field size in blocks.</param>
    /// <param name="thickness">Height of the stacked layers, for the cull AABB.</param>
    /// <param name="offsets">Destination, at least <see cref="MaxCopies"/> long.</param>
    /// <exception cref="ArgumentException">The destination is too short to hold every copy.</exception>
    internal static int Gather(in Frustum frustum, float snapX, float snapZ, Vector2 drift, float field,
        float thickness, Span<Vector2> offsets)
    {
        if (offsets.Length < MaxCopies)
            throw new ArgumentException(
                $"Destination must hold at least {MaxCopies} copies, got {offsets.Length}.", nameof(offsets));

        var count = 0;
        for (var ix = -1; ix <= 1; ix++)
        for (var iz = -1; iz <= 1; iz++)
        {
            var minX = snapX + ix * field + drift.X;
            var minZ = snapZ + iz * field + drift.Y;
            var min = new Vector3(minX, WorldGenerationConfig.CloudHeight, minZ);
            if (!frustum.IntersectsAABB(min, min + new Vector3(field, thickness, field)))
                continue;

            offsets[count++] = new Vector2(minX, minZ);
        }

        return count;
    }
}
