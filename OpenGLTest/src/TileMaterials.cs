using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// How reflective one atlas tile's surface is, from the owning block's <c>material</c> JSON.
/// See <see cref="TileMaterials"/> for why this is keyed by tile rather than by block.
/// </summary>
/// <param name="Metallic">0 is an ordinary diffuse block; higher trades diffuse albedo for an
/// albedo-tinted specular.</param>
/// <param name="Roughness">0..1, lower being a tighter highlight.</param>
/// <param name="NormalStrength">Multiplier on the tile's normal map relief.</param>
public readonly record struct TileMaterial(float Metallic, float Roughness, float NormalStrength)
{
    /// <summary>What every tile no block declared a material for reads as: perfectly diffuse,
    /// which makes the fragment shader's <c>metallic &gt; 0</c> branch skip the whole path.</summary>
    public static readonly TileMaterial Diffuse = new(0f, 1f, 0f);

    /// <summary>Whether this tile wants the reflective path at all.</summary>
    public bool IsReflective => Metallic > 0f;
}

/// <summary>
/// The per-atlas-layer material table the world fragment shader indexes by <c>fLayer</c>.
///
/// Material is keyed by <em>tile</em>, not by block, and that is what keeps the whole feature off
/// the vertex: a tile index already rides every vertex as <see cref="Vertex.Layer"/>, so no new
/// vertex attribute, no wider vertex, no change to the greedy merge key and no change to the
/// meshing tests are needed. The cost is that two blocks sharing one texture also share its
/// material, which <see cref="Build"/> reports rather than silently resolving.
///
/// Startup-owned, exactly like <see cref="TextureAtlas"/>: <see cref="Build"/> runs once from
/// <see cref="ContentRegistry.BakeModels"/>, before the <see cref="World"/> and its worker
/// threads exist, and the table is read-only afterwards.
/// </summary>
public static class TileMaterials
{
    /// <summary>One entry per atlas slice, in the atlas's own slice order. Empty before
    /// <see cref="Build"/>.</summary>
    public static TileMaterial[] ByLayer { get; private set; } = [];

    /// <summary>Whether any tile at all is reflective. False lets the renderer skip uploading
    /// and binding the material buffer entirely.</summary>
    public static bool AnyReflective { get; private set; }

    /// <summary>Material of one atlas slice; diffuse for any slice outside the table.</summary>
    public static TileMaterial Of(int layer) =>
        (uint)layer < (uint)ByLayer.Length ? ByLayer[layer] : TileMaterial.Diffuse;

    internal static void Reset()
    {
        ByLayer = [];
        AnyReflective = false;
    }

    /// <summary>
    /// Spreads each block's declared material across the tiles its faces use. A block that
    /// declares nothing leaves its tiles diffuse, so only the ores end up reflective.
    /// </summary>
    internal static void Build(int layerCount, IEnumerable<BlockType> blocks)
    {
        var table = new TileMaterial[layerCount];
        Array.Fill(table, TileMaterial.Diffuse);

        // Which block wrote each tile, so a genuine disagreement can name both files.
        var owners = new string?[layerCount];
        var any = false;

        foreach (var type in blocks)
        {
            if (type.Material is not { } material) continue;

            foreach (var tile in type.FaceTiles)
            {
                if (tile >= layerCount) continue;
                var owner = owners[tile];
                if (owner != null && owner != type.Name && table[tile] != material)
                {
                    Log.Warn($"Blocks '{owner}' and '{type.Name}' share a texture but declare " +
                             $"different materials; '{type.Name}' wins for that tile.");
                }

                table[tile] = material;
                owners[tile] = type.Name;
                any |= material.IsReflective;
            }
        }

        ByLayer = table;
        AnyReflective = any;
    }

    /// <summary>
    /// The table flattened for the shader's SSBO: one <c>vec4</c> per slice, laid out
    /// <c>(metallic, roughness, normalStrength, 0)</c> so std430 needs no padding.
    /// </summary>
    public static float[] ToBufferData()
    {
        var data = new float[ByLayer.Length * 4];
        for (var i = 0; i < ByLayer.Length; i++)
        {
            data[i * 4 + 0] = ByLayer[i].Metallic;
            data[i * 4 + 1] = ByLayer[i].Roughness;
            data[i * 4 + 2] = ByLayer[i].NormalStrength;
        }
        return data;
    }
}
