namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>The optional integer deep axis; default keeps synthetic S5a calderas surface-only.</summary>
internal readonly record struct VolcanoDeep(bool Exists, int AxisX, int AxisZ);
