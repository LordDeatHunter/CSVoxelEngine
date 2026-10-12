using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>Shader.InjectDefines</c> lets one GLSL file be compiled as two program variants (the plain
/// one and the <c>#define CHUNK_DRAW</c> pooled chunk-draw one). GLSL requires <c>#version</c> to
/// be the first line, so the prefix is spliced in after it rather than prepended, and a
/// <c>#line 2</c> keeps compiler error line numbers pointing at the file on disk.
/// </summary>
public sealed class ShaderDefinesTests
{
    [Fact]
    public void InjectsAfterVersionLineWithLineDirective()
    {
        var result = Shader.InjectDefines("#version 460 core\nvoid main(){}\n", "#define CHUNK_DRAW");

        Assert.Equal("#version 460 core\n#define CHUNK_DRAW\n#line 2\nvoid main(){}\n", result);
    }

    [Fact]
    public void KeepsCrlfVersionLine()
    {
        var result = Shader.InjectDefines("#version 460 core\r\nvoid main(){}\r\n", "#define CHUNK_DRAW");

        var lines = result.Split('\n');
        Assert.Equal("#version 460 core\r", lines[0]);
        Assert.Equal("#define CHUNK_DRAW", lines[1]);
        Assert.Equal("#line 2", lines[2]);
        Assert.Equal("void main(){}\r", lines[3]);
    }

    [Fact]
    public void NoDefinesReturnsSourceUnchanged()
    {
        const string source = "#version 460 core\nvoid main(){}\n";

        Assert.Same(source, Shader.InjectDefines(source, null));
        Assert.Same(source, Shader.InjectDefines(source, ""));
    }
}
