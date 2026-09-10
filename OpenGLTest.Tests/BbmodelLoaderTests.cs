using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class BbmodelLoaderTests
{
    private const string Path = "player.bbmodel";

    private static JsonObject FaceObj(double x1, double y1, double x2, double y2, double? rotation = null)
    {
        var face = new JsonObject
        {
            ["uv"] = new JsonArray(x1, y1, x2, y2),
            ["texture"] = 0,
        };
        if (rotation.HasValue)
            face["rotation"] = rotation.Value;
        return face;
    }

    private static string MinimalModel(string? textureSource = null, bool rotatedCube = false,
        double? faceRotation = null)
    {
        var outliner = new JsonArray();
        var elements = new JsonArray();
        var i = 0;
        foreach (var name in BbmodelLoader.RequiredParts)
        {
            var uuid = $"00000000-0000-0000-0000-{i:D12}";
            // Pivots stand on y = 0 with 16 units per block, like the real file.
            outliner.Add(new JsonObject
            {
                ["name"] = name,
                ["origin"] = new JsonArray(0, 12, 0),
                ["rotation"] = new JsonArray(0, 0, 0),
                ["children"] = new JsonArray(uuid),
            });
            elements.Add(new JsonObject
            {
                ["name"] = "cube",
                ["uuid"] = uuid,
                ["from"] = new JsonArray(0, 12, -2),
                ["to"] = new JsonArray(4, 22, 2),
                ["faces"] = new JsonObject
                {
                    ["north"] = FaceObj(0, 0, 16, 16, faceRotation),
                    ["up"] = FaceObj(16, 0, 32, 16),
                },
                ["type"] = "cube",
                ["rotation"] = new JsonObject
                {
                    ["angle"] = rotatedCube ? 45 : 0,
                    ["axis"] = "y",
                    ["origin"] = new JsonArray(0, 0, 0),
                },
            });
            i++;
        }
        var root = new JsonObject
        {
            ["meta"] = new JsonObject { ["format_version"] = "4.0" },
            ["resolution"] = new JsonObject { ["width"] = 64, ["height"] = 64 },
            ["outliner"] = outliner,
            ["elements"] = elements,
        };
        if (textureSource != null)
            root["textures"] = new JsonArray(new JsonObject { ["source"] = textureSource });
        return root.ToJsonString();
    }

    private static byte[] TestPng()
    {
        const int w = 2;
        const int h = 2;
        // Red, green / blue, white: decode order pins the pixel layout assert below.
        var px = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
        using var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(px, y * w * 4, w * 4);
        }
        var rawBytes = raw.ToArray();
        using var def = new MemoryStream();
        using (var ds = new DeflateStream(def, CompressionMode.Compress, leaveOpen: true))
            ds.Write(rawBytes, 0, rawBytes.Length);
        var comp = def.ToArray();

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10], 0, 8);
        void Chunk(string type, byte[] data)
        {
            WriteBe32(png, data.Length);
            var t = Encoding.ASCII.GetBytes(type);
            png.Write(t, 0, t.Length);
            png.Write(data, 0, data.Length);
            using var buf = new MemoryStream();
            buf.Write(t, 0, t.Length);
            buf.Write(data, 0, data.Length);
            WriteBe32(png, Crc32(buf.ToArray()));
        }
        using var ihdr = new MemoryStream();
        WriteBe32(ihdr, w);
        WriteBe32(ihdr, h);
        ihdr.Write([8, 6, 0, 0, 0], 0, 5);
        Chunk("IHDR", ihdr.ToArray());
        using var idat = new MemoryStream();
        idat.Write([0x78, 0x01], 0, 2);
        idat.Write(comp, 0, comp.Length);
        var adler = Adler32(rawBytes);
        idat.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler], 0, 4);
        Chunk("IDAT", idat.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }

    private static void WriteBe32(Stream s, long v)
    {
        s.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v], 0, 4);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (uint)i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) == 1 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void CrcTableMatchesCheckVector()
    {
        Assert.Equal(0xCBF43926u, Crc32(Encoding.ASCII.GetBytes("123456789")));
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        foreach (var byte_ in data)
        {
            a = (a + byte_) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    [Fact]
    public void ParsesPivotsCubesAndUvs()
    {
        var data = BbmodelLoader.Parse(MinimalModel(), Path, _ => throw new NotSupportedException());

        Assert.Null(data.TexturePixels);
        var torso = data.Parts["Torso"];
        Assert.Equal(new Vector3(0f, 0.75f, 0f), torso.Pivot);
        var cube = Assert.Single(torso.Cubes);
        // Pivot-relative: from [0,12,-2] units is [0,0.75,-0.125] blocks minus the pivot.
        Assert.Equal(new Vector3(0f, 0f, -0.125f), cube.Min);
        Assert.Equal(new Vector3(0.25f, 0.625f, 0.125f), cube.Max);
        // Normalized by the 64px resolution: north (0,0,16,16) covers the top-left quarter.
        Assert.Equal(new Vector2(0f, 0f), cube.Faces[0]!.Value.Min);
        Assert.Equal(new Vector2(0.25f, 0.25f), cube.Faces[0]!.Value.Max);
        // Omitted faces stay null instead of inventing geometry.
        Assert.Null(cube.Faces[1]);
        Assert.Null(cube.Faces[5]);
    }

    [Fact]
    public void MissingGroupNamesTheFile()
    {
        var json = MinimalModel().Replace("\"UpperArmL\"", "\"ArmTypo\"");
        var e = Assert.Throws<InvalidOperationException>(() => BbmodelLoader.Parse(json, Path, _ => []));
        Assert.Contains(Path, e.Message);
        Assert.Contains("UpperArmL", e.Message);
    }

    [Fact]
    public void RotatedCubeAndFaceNameTheFile()
    {
        Assert.Contains(Path, Assert.Throws<InvalidOperationException>(
            () => BbmodelLoader.Parse(MinimalModel(rotatedCube: true), Path, _ => [])).Message);

        Assert.Contains(Path, Assert.Throws<InvalidOperationException>(
            () => BbmodelLoader.Parse(MinimalModel(faceRotation: 90), Path, _ => [])).Message);
    }

    [Fact]
    public void RelativeTextureLoadsThroughTheStub()
    {
        var png = TestPng();
        string? seen = null;
        var data = BbmodelLoader.Parse(MinimalModel(textureSource: "skin.png"), "models/player.bbmodel",
            p => { seen = p; return png; });

        Assert.EndsWith("skin.png", seen);
        Assert.Equal(2, data.TextureWidth);
        Assert.Equal(2, data.TextureHeight);
        // Top-first RGBA: top-left texel is the red one the builder wrote first.
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, data.TexturePixels![0..4]);
    }

    [Fact]
    public void EmbeddedTextureDecodes()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(TestPng());
        var data = BbmodelLoader.Parse(MinimalModel(textureSource: uri), Path, _ => throw new NotSupportedException());
        Assert.Equal(2, data.TextureWidth);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, data.TexturePixels![8..12]);
    }

    [Fact]
    public void FromBbmodelFallsBackToPaletteWithoutTexture()
    {
        var rig = PlayerModel.PlayerRig.FromBbmodel(BbmodelLoader.Parse(MinimalModel(), Path, _ => []));
        Assert.Equal(PlayerModel.PaletteCols, rig.TextureWidth);
        Assert.Equal(PlayerModel.PaletteRows, rig.TextureHeight);
        Assert.Equal(10, rig.Parts.Count);
    }

    [Fact]
    public void BuiltInRigMatchesLegacyConsts()
    {
        var rig = PlayerModel.PlayerRig.BuiltIn();
        Assert.Equal(new Vector3(0f, PlayerModel.HipY, 0f), rig.Parts["Torso"].Pivot);
        Assert.Equal(new Vector3(0f, PlayerModel.NeckY, 0f), rig.Parts["Head"].Pivot);
        Assert.Equal(new Vector3(PlayerModel.ShoulderX, PlayerModel.ShoulderY, 0f), rig.Parts["UpperArmR"].Pivot);
        Assert.Equal(new Vector3(-PlayerModel.HipX, PlayerModel.HipY - PlayerModel.ThighLength, 0f),
            rig.Parts["ShinL"].Pivot);
        // Elbow/knee offsets the pose code derives from pivots equal the old lengths.
        Assert.Equal(new Vector3(0f, -PlayerModel.UpperArmLength, 0f),
            rig.Parts["ForearmR"].Pivot - rig.Parts["UpperArmR"].Pivot);
    }

    [Fact]
    public void InvalidJsonNamesTheFile()
    {
        var e = Assert.Throws<InvalidOperationException>(() => BbmodelLoader.Parse("{nope", Path, _ => []));
        Assert.Contains(Path, e.Message);
    }
}
