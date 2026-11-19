namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>One caldera volume material; none leaves the previous terrain untouched.</summary>
internal enum VolcanoCell : byte { None, Lining, Lava, Air }
