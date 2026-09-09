using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Phase 4 compiles the two chunk vertex shaders twice from one source each: the plain program
/// (uModel/uMVP/uAlpha) that entities, LOD, drops, TNT and block pops still draw with, and a
/// <c>#define CHUNK_DRAW</c> variant whose per-draw origin and fade alpha come out of the
/// ChunkInfo SSBO at <c>uDrawBase + gl_DrawID</c> (see ChunkDrawBatch). These read the shader
/// files copied next to the test binary — the same ones the game ships — and pin the three
/// things a silent GLSL edit could break without any test noticing:
/// the leaf-sway block staying byte-identical between the world and shadow shaders (or leaf
/// shadows drift away from the leaves casting them), the CHUNK_DRAW plumbing being present in
/// both, and the fragment stage taking the fade as a varying rather than a uniform — a uniform
/// cannot vary between the sub-draws of one glMultiDrawElementsIndirect call.
/// </summary>
public sealed class ChunkDrawShaderTests
{
    private const string SwayOpen = "if (vSway > 0.001 && uSwayStrength > 0.001) {";
    private const string SwayClose = "    }";

    private static string ReadShader(string name) =>
        File.ReadAllText(Path.Combine(TestContent.FindAssetsRoot(), "shaders", name));

    /// <summary>
    /// Pulls the sway block out of a vertex shader: the line opening it through the first
    /// following line that is exactly its closing brace. Endings are normalised so the
    /// comparison is about the GLSL, not about CRLF/LF drift between the two files.
    /// </summary>
    private static string ExtractSwayBlock(string source, string name)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains(SwayOpen));
        Assert.True(start >= 0, $"{name} has no line containing \"{SwayOpen}\"");

        var block = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            block.Add(lines[i]);
            if (i > start && lines[i] == SwayClose) return string.Join("\n", block);
        }

        Assert.Fail($"{name}: the sway block starting at line {start + 1} is never closed by \"{SwayClose}\"");
        return string.Empty;
    }

    [Fact]
    public void SwayBlocksStayIdenticalAcrossChunkShaders()
    {
        var world = ExtractSwayBlock(ReadShader("test-vertex.glsl"), "test-vertex.glsl");
        var shadow = ExtractSwayBlock(ReadShader("shadow-vertex.glsl"), "shadow-vertex.glsl");

        Assert.Equal(world, shadow);
    }

    [Fact]
    public void ChunkDrawVariantIsDeclaredInBothVertexShaders()
    {
        string[] required = ["#ifdef CHUNK_DRAW", "gl_DrawID", "uDrawBase", "binding = 0", "uViewProj"];

        foreach (var name in new[] { "test-vertex.glsl", "shadow-vertex.glsl" })
        {
            var source = ReadShader(name);
            foreach (var token in required)
                Assert.True(source.Contains(token), $"{name} is missing \"{token}\"");
        }
    }

    [Fact]
    public void FragmentTakesAlphaAsVarying()
    {
        var source = ReadShader("test-fragment.glsl");

        Assert.Contains("flat in float fAlpha;", source);
        Assert.DoesNotContain("uniform float uAlpha;", source);
    }
}
