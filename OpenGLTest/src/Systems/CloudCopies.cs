using System;
using System.Numerics;

namespace silkgltriangle;

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
/// colour/depth mask toggles, and the per-copy submission — which is only reachable by running the
/// game.
///
/// Two properties are worth stating because they are what make the caller correct:
///
/// <list type="bullet">
/// <item>Copies snap to field multiples (<see cref="Snap"/>) so tiles never swim under the camera,
/// and drift is a whole-copy translation wrapped modulo the field (<see cref="Drift"/>) — the noise
/// and the mesh both wrap at the field edge, so the wrap is seamless and nothing moves per
/// vertex.</item>
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
    /// How far the whole layer has drifted this frame, wrapped into [0, field). The pattern repeats
    /// every field, so wrapping the translation is invisible.
    /// </summary>
    internal static float Drift(float time, float field) =>
        (time * WorldGenerationConfig.CloudDriftBlocksPerSecond) % field;

    /// <summary>
    /// The field multiple at or below <paramref name="coord"/>. Floor, not truncation: at negative
    /// coordinates truncation would round towards zero and snap the grid a whole field out of place.
    /// </summary>
    internal static float Snap(float coord, float field) => MathF.Floor(coord / field) * field;

    /// <summary>
    /// Fills <paramref name="offsets"/> with the world-space (x, z) origin of every copy of the 3x3
    /// that survives the frustum cull, packed to the front in visit order, and returns how many
    /// there are. Each copy is tested as the AABB spanning one field in x and z and the full stack
    /// thickness in y, based at <see cref="WorldGenerationConfig.CloudHeight"/>.
    /// </summary>
    /// <param name="frustum">Culling frustum, built from the pass's view-projection.</param>
    /// <param name="snapX">Camera x snapped to a field multiple, from <see cref="Snap"/>.</param>
    /// <param name="snapZ">Camera z snapped to a field multiple, from <see cref="Snap"/>.</param>
    /// <param name="drift">This frame's wrapped drift, from <see cref="Drift"/>.</param>
    /// <param name="field">Field size in blocks.</param>
    /// <param name="thickness">Height of the stacked layers, for the cull AABB.</param>
    /// <param name="offsets">Destination, at least <see cref="MaxCopies"/> long.</param>
    /// <exception cref="ArgumentException">The destination is too short to hold every copy.</exception>
    internal static int Gather(in Frustum frustum, float snapX, float snapZ, float drift, float field,
        float thickness, Span<Vector2> offsets)
    {
        if (offsets.Length < MaxCopies)
            throw new ArgumentException(
                $"Destination must hold at least {MaxCopies} copies, got {offsets.Length}.", nameof(offsets));

        var count = 0;
        for (var ix = -1; ix <= 1; ix++)
        for (var iz = -1; iz <= 1; iz++)
        {
            var minX = snapX + ix * field + drift;
            var minZ = snapZ + iz * field;
            var min = new Vector3(minX, WorldGenerationConfig.CloudHeight, minZ);
            if (!frustum.IntersectsAABB(min, min + new Vector3(field, thickness, field)))
                continue;

            offsets[count++] = new Vector2(minX, minZ);
        }

        return count;
    }
}
