using System;
using System.IO;
using System.Linq;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// The imported-map column format (<see cref="ColumnFileCodec"/>): a column decodes to exactly the
/// terrain it was written from, with the derived bounds recomputed, and a file that is cut short,
/// is not a column file or names a biome that does not exist is refused as
/// <see cref="InvalidDataException"/> instead of becoming terrain.
/// </summary>
public class ColumnFileCodecTests
{
    private const int ColumnArea = Chunk.Size * Chunk.Size;

    private static byte[] Encode(ColumnData column)
    {
        using var stream = new MemoryStream();
        ColumnFileCodec.Write(stream, column);
        return stream.ToArray();
    }

    private static ColumnData Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return ColumnFileCodec.Read(stream);
    }

    // Varied terrain: heights on both sides of zero, a bed at or above every height (ravines cut
    // the heights below it), dry and wet cells, every biome, bare rock and deep soil, and climate
    // floats with no short binary form.
    private static ColumnData VariedColumn(Biome[]? biomesOverride = null)
    {
        var random = new Random(1234);
        var biomeValues = Enum.GetValues<Biome>();
        var heights = new int[ColumnArea];
        var bed = new int[ColumnArea];
        var water = new int[ColumnArea];
        var biomes = new Biome[ColumnArea];
        var dirt = new byte[ColumnArea];
        for (var i = 0; i < ColumnArea; i++)
        {
            heights[i] = random.Next(-200, 400);
            bed[i] = heights[i] + random.Next(0, 12);
            water[i] = i % 3 == 0 ? TerrainGenerator.NoWater : bed[i] + random.Next(1, 20);
            biomes[i] = biomeValues[i % biomeValues.Length];
            dirt[i] = (byte)random.Next(0, 9);
        }

        return new ColumnData(heights, dirt, new float[ColumnArea], new float[ColumnArea], biomesOverride ?? biomes,
            bed, water, cornerWarmth: MathF.PI / 7f, cornerWetness: 1f / 3f);
    }

    [Fact]
    public void AColumnRoundTripsBitForBit()
    {
        var column = VariedColumn();
        // The fixture really exercises every biome and both wet and dry cells
        Assert.Equal(Enum.GetValues<Biome>().Length, column.Biomes.Distinct().Count());
        Assert.Contains(TerrainGenerator.NoWater, column.WaterLevel);
        Assert.Contains(column.WaterLevel, level => level != TerrainGenerator.NoWater);

        var back = Decode(Encode(column));

        Assert.Equal(column.Heights, back.Heights);
        Assert.Equal(column.Bed, back.Bed);
        Assert.Equal(column.WaterLevel, back.WaterLevel);
        Assert.Equal(column.Biomes, back.Biomes);
        Assert.Equal(column.DirtDepth, back.DirtDepth);
        Assert.Equal(BitConverter.SingleToInt32Bits(column.CornerWarmth), BitConverter.SingleToInt32Bits(back.CornerWarmth));
        Assert.Equal(BitConverter.SingleToInt32Bits(column.CornerWetness), BitConverter.SingleToInt32Bits(back.CornerWetness));

        // The bounds are derived from the decoded arrays, not stored, and agree with the original's
        Assert.Equal(column.MinHeight, back.MinHeight);
        Assert.Equal(column.MaxHeight, back.MaxHeight);
        Assert.Equal(column.MaxWaterLevel, back.MaxWaterLevel);

        // The carvers' inputs are not part of the format: a decoded column has none of them
        Assert.Equal(ColumnArea, back.EntranceWeight.Length);
        Assert.Equal(ColumnArea, back.CaveIntensity.Length);
        Assert.All(back.EntranceWeight, w => Assert.Equal(0f, w));
        Assert.All(back.CaveIntensity, c => Assert.Equal(0f, c));
    }

    [Fact]
    public void ATruncatedOrForeignFileIsRefused()
    {
        var bytes = Encode(VariedColumn());

        // Cut in the middle of the compressed body, and cut inside the header
        Assert.Throws<InvalidDataException>(() => Decode(bytes[..(bytes.Length / 2)]));
        Assert.Throws<InvalidDataException>(() => Decode(bytes[..3]));

        // Another file's magic
        var foreign = (byte[])bytes.Clone();
        foreign[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => Decode(foreign));

        // A version this build does not read
        var future = (byte[])bytes.Clone();
        future[4] = unchecked((byte)(ColumnFileCodec.Version + 1));
        Assert.Throws<InvalidDataException>(() => Decode(future));

        // A biome byte that names no biome
        var undefined = Enumerable.Range(0, 256).Select(b => (Biome)b).First(b => !Enum.IsDefined(b));
        var biomes = Enumerable.Repeat(Biome.Forest, ColumnArea).ToArray();
        biomes[ColumnArea / 2] = undefined;
        var badBiome = Encode(VariedColumn(biomes));
        Assert.Throws<InvalidDataException>(() => Decode(badBiome));
    }
}
