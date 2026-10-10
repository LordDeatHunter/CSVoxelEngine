using System;
using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The world and shadow vertex shaders each displace swaying vertices (canopy leaves, rooted
/// plants) by the wind. A shadow must move with the geometry that casts it, so the sway weight
/// decode and the displacement block have to stay byte-identical between the two files; the
/// <c>TO_WORLD(p)</c> macro is what lets them be. A drift compiles and links fine and only shows
/// as leaf or grass shadows sliding away from their casters, so this is its one guard.
/// </summary>
public sealed class ChunkDrawShaderTests
{
    private static string Shader(string name) =>
        File.ReadAllText(Path.Combine(TestContent.FindAssetsRoot(), "shaders", name)).Replace("\r\n", "\n");

    /// <summary>The line of <paramref name="source"/> that starts (after indentation) with
    /// <paramref name="prefix"/>.</summary>
    private static string LineStartingWith(string source, string prefix)
    {
        foreach (var line in source.Split('\n'))
            if (line.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                return line;
        throw new Xunit.Sdk.XunitException($"no line starts with '{prefix}'");
    }

    /// <summary>From the first <paramref name="opening"/> to its matching closing brace.</summary>
    private static string BracedBlock(string source, string opening)
    {
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{opening}' not found");
        var depth = 0;
        for (var i = source.IndexOf('{', start); i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new Xunit.Sdk.XunitException($"'{opening}' never closes");
    }

    [Fact]
    public void TheSwayBlockIsIdenticalInTheWorldAndShadowVertexShaders()
    {
        var world = Shader("test-vertex.glsl");
        var shadow = Shader("shadow-vertex.glsl");

        // The weight decode: which vertices sway and by how much
        Assert.Equal(LineStartingWith(world, "bool swaying"), LineStartingWith(shadow, "bool swaying"));
        Assert.Equal(LineStartingWith(world, "float vSway"), LineStartingWith(shadow, "float vSway"));
        // Both read the weight from the same attribute
        Assert.Equal(LineStartingWith(world, "layout (location = 1)"), LineStartingWith(shadow, "layout (location = 1)"));

        // The displacement itself
        const string sway = "if (vSway > 0.001";
        Assert.Equal(BracedBlock(world, sway), BracedBlock(shadow, sway));
    }
}
