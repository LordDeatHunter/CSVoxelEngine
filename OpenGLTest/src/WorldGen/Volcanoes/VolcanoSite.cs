namespace VoxelEngine.WorldGen.Volcanoes;

/// <summary>An immutable accepted Dynamic volcano. Default means a rejected site cell.</summary>
internal readonly record struct VolcanoSite(bool Exists, float X, float Z, float Radius,
    float CraterRadius, int Floor, int LakeDepth, float Rise);
