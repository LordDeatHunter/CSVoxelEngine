using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The engine requests an OpenGL 4.6 core context (see <c>WindowManager</c>), so every shader
/// must declare <c>#version 460 core</c> on its first line. This reads the shaders copied next
/// to the test binary — the same files the game ships — through File.ReadAllText, exactly the
/// call FileUtils.ReadFile uses, so the folder's mixed CRLF/LF endings and the UTF-8 BOM on
/// selection-vertex.glsl are pinned as the engine (and the GL compiler) actually see them.
/// </summary>
public sealed class ShaderVersionTests
{
    [Fact]
    public void EveryShaderDeclaresVersion460Core()
    {
        var dir = Path.Combine(TestContent.FindAssetsRoot(), "shaders");
        var files = Directory.GetFiles(dir, "*.glsl");
        var wrong = new List<string>();
        var checkedCount = 0;

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) continue; // selection-frag.glsl is a BOM-only orphan with no source
            checkedCount++;
            var first = text.Split('\n')[0].TrimEnd('\r').Trim();
            if (first != "#version 460 core") wrong.Add(Path.GetFileName(path));
        }

        Assert.True(checkedCount >= 16, $"only {checkedCount} shaders found under {dir}");
        Assert.True(wrong.Count == 0, "Shaders not declaring '#version 460 core': " + string.Join(", ", wrong));
    }
}
