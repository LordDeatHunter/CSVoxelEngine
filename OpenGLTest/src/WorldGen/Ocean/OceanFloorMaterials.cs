namespace VoxelEngine.WorldGen.Ocean;

/// <summary>Pure habitat-to-floor rule shared by near generation and generated LOD.</summary>
internal static class OceanFloorMaterials
{
    internal static OceanFloorMaterial Select(OceanHabitat habitat, float warmth, int depth) =>
        habitat switch
        {
            OceanHabitat.Reef => OceanFloorMaterial.Coral,
            OceanHabitat.MeadowFloor => OceanFloorMaterial.SeaMoss,
            OceanHabitat.DeepPlain or OceanHabitat.Abyss when warmth >= 0.53f && depth >= 40
                => OceanFloorMaterial.GlowMoss,
            _ => OceanFloorMaterial.None,
        };

    /// <summary>Generated-only LOD codes; existing floor codes remain 0..7.</summary>
    internal static byte LodCode(OceanFloorMaterial material) => material switch
    {
        OceanFloorMaterial.Coral => 8,
        OceanFloorMaterial.SeaMoss => 9,
        OceanFloorMaterial.GlowMoss => 10,
        _ => 2,
    };
}
