namespace silkgltriangle;

/// <summary>
/// A model turned into chunk-ready geometry: atlas slices resolved, UVs tile-local, element rotations applied and
/// full-face coverage worked out. Built once at startup by <see cref="ModelBaker"/> and then
/// read-only, so meshing threads can share it.
/// </summary>
/// <param name="Quads">Every quad of the model, in cell-local space.</param>
/// <param name="IsFullCube">Whether all six cube faces are fully covered, which is what lets a
/// block occlude its neighbors and block light by default.</param>
/// <param name="HasFullFace">Per engine face index, whether that cube face is fully covered.</param>
/// <param name="FaceTiles">Per engine face index, the tile of that side's full face — or the
/// first quad's tile as a fallback, so tile-based emitters (LOD, pebbles, tree wood) always
/// have something sensible to sample.</param>
public sealed record BakedModel(
    BakedQuad[] Quads,
    bool IsFullCube,
    bool[] HasFullFace,
    ushort[] FaceTiles);
