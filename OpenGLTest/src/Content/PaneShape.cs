using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The one shape rule of a <see cref="BlockRenderer.Pane"/> block (the glass pane, the fence, the
/// wall), derived from its four horizontal neighbours at mesh time and never stored: a full-height
/// centre post, plus an arm reaching the cell edge toward every neighbour it connects to
/// (<see cref="ConnectsTo(PaneProfile, byte, byte)"/>: a pane of the same shape, or a solid full
/// cube). A lone pane is just the post, as in Minecraft. How wide the post and arms are and which
/// height bands an arm fills is the block's <see cref="PaneProfile"/> (its <c>pane</c> JSON; the
/// glass pane's 2 px post and arms are <see cref="PaneProfile.Default"/>). Directions are the
/// horizontal <see cref="Chunk.CubeFaces"/> indices (north -Z = 0, south +Z = 1, west -X = 2,
/// east +X = 3), and bit <c>d</c> of a mask is an arm toward direction <c>d</c>. The mesher
/// (<see cref="PaneEmitter"/>), the raycast, the outline and body collision
/// (<see cref="BlockShape"/>) all read this, so what is drawn is exactly what is hit.
/// GL-free and allocation-free. The members without a profile describe the default shape.
/// </summary>
public static class PaneShape
{
    /// <summary>The default post's and every default arm's near and far edge across the pane,
    /// cell-local: the middle 2 of 16 pixels.</summary>
    public const float Near = 7f / 16f;

    /// <inheritdoc cref="Near"/>
    public const float Far = 9f / 16f;

    /// <summary>Number of horizontal directions, and so of arms.</summary>
    public const int Directions = 4;

    /// <summary>The shape of a pane block id: its <see cref="BlockType.PaneProfileById"/> entry,
    /// or the default for anything else.</summary>
    public static PaneProfile ProfileOf(byte id) => BlockType.PaneProfileById[id] ?? PaneProfile.Default;

    /// <summary>
    /// Whether a pane of shape <paramref name="profile"/> reaches toward a neighbour holding
    /// <paramref name="id"/> with state byte <paramref name="state"/>: a pane of the same shape,
    /// or a solid full cube - one that occludes, a plain full-cube model such as glass or leaves,
    /// or full-thickness tree wood. Air, fluids, sub-cell blocks (a torch, a flower, thin wood) and
    /// panes of another shape (a fence beside a glass pane) get no arm.
    /// </summary>
    public static bool ConnectsTo(PaneProfile profile, byte id, byte state) =>
        BlockType.IsPane(id)
            ? ReferenceEquals(ProfileOf(id), profile)
            : BlockType.IsSolid(id)
              && (BlockType.Occludes(id) || BlockType.IsGreedy(id)
                  || (BlockType.IsTreeWood(id) && WoodState.IsFullCube(state)));

    /// <summary><see cref="ConnectsTo(PaneProfile, byte, byte)"/> for the default shape.</summary>
    public static bool ConnectsTo(byte id, byte state) => ConnectsTo(PaneProfile.Default, id, state);

    /// <summary>The arm mask of a pane of shape <paramref name="profile"/> whose horizontal
    /// neighbours, in direction order, hold <paramref name="ids"/> with <paramref name="states"/>
    /// (at least four of each).</summary>
    public static int ConnectionMask(PaneProfile profile, ReadOnlySpan<byte> ids, ReadOnlySpan<byte> states)
    {
        var mask = 0;
        for (var dir = 0; dir < Directions; dir++)
            if (ConnectsTo(profile, ids[dir], states[dir]))
                mask |= 1 << dir;
        return mask;
    }

    /// <summary><see cref="ConnectionMask(PaneProfile, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// for the default shape.</summary>
    public static int ConnectionMask(ReadOnlySpan<byte> ids, ReadOnlySpan<byte> states) =>
        ConnectionMask(PaneProfile.Default, ids, states);

    /// <summary>The arm mask of the pane at world cell (x, y, z), its shape read from the cell
    /// and its neighbours through <paramref name="chunks"/> (an unloaded neighbour reads as air:
    /// no arm).</summary>
    public static int ConnectionMaskAt(ChunkManager chunks, int x, int y, int z)
    {
        var profile = ProfileOf(chunks.GetBlockGlobal(x, y, z));
        var mask = 0;
        for (var dir = 0; dir < Directions; dir++)
        {
            var normal = Chunk.CubeFaces[dir].normal;
            int nx = x + (int)normal.X, nz = z + (int)normal.Z;
            if (ConnectsTo(profile, chunks.GetBlockGlobal(nx, y, nz), chunks.GetStateGlobal(nx, y, nz)))
                mask |= 1 << dir;
        }
        return mask;
    }

    /// <summary>The default full-height centre post, cell-local.</summary>
    public static void PostBox(out Vector3 min, out Vector3 max) => PaneProfile.Default.PostBox(out min, out max);

    /// <summary>The default full-height arm from the post to the cell edge toward
    /// <paramref name="dir"/>, cell-local.</summary>
    public static void ArmBox(int dir, out Vector3 min, out Vector3 max) =>
        PaneProfile.Default.ArmBox(dir, 0, out min, out max);

    /// <summary>The selection and collision boxes of a default-shaped pane with arm mask
    /// <paramref name="mask"/> (see <see cref="PaneProfile.Boxes"/>). The arrays are shared;
    /// never write into them.</summary>
    public static BlockHitbox[] Boxes(int mask) => PaneProfile.Default.Boxes(mask);

    /// <summary>The boxes of the pane at world cell (x, y, z), in its own shape.</summary>
    public static BlockHitbox[] GetBoxesAt(ChunkManager chunks, int x, int y, int z) =>
        ProfileOf(chunks.GetBlockGlobal(x, y, z)).Boxes(ConnectionMaskAt(chunks, x, y, z));
}
