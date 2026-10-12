using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Content.Models;

/// <summary>
/// One textured quad of a baked model, in cell-local space (0..1 across the block).
/// </summary>
/// <param name="Positions">Four corners in <see cref="Chunk.CubeFaces"/> corner order for
/// <see cref="Face"/>, so per-corner ambient occlusion lines up with the full-cube path.</param>
/// <param name="Uvs">Tile-local UVs (0..1 across the tile) matching <paramref name="Positions"/>
/// corner for corner; <paramref name="Tile"/> is the atlas slice they sample.</param>
/// <param name="Normal">Unit face normal.</param>
/// <param name="Face">Dominant normal axis as an engine face index: -Z=0, +Z=1, -X=2, +X=3,
/// +Y=4, -Y=5.</param>
/// <param name="CullFace">Cell side this quad is culled against, or -1 when it always draws.</param>
/// <param name="IsFullFace">Whether the quad exactly covers its cube face, which is what makes
/// per-corner AO and neighbor-cell lighting meaningful.</param>
/// <param name="Shade">Whether directional shading applies (Minecraft's per-element
/// <c>shade</c>).</param>
/// <param name="Tile">Atlas slice the UVs sample.</param>
public sealed record BakedQuad(
    Vector3[] Positions,
    Vector2[] Uvs,
    Vector3 Normal,
    int Face,
    int CullFace,
    bool IsFullFace,
    bool Shade,
    ushort Tile);
