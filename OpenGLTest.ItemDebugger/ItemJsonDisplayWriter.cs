using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace VoxelEngine.ItemDebugger;

/// <summary>
/// Writes an item's <c>display</c> block back into its JSON file. Only the display value's
/// byte range is replaced (or one inserted before the closing brace), so comments, key order
/// and every other field survive a save. Reads with the same tolerance the loader uses
/// (comments and trailing commas allowed).
/// </summary>
public static class ItemJsonDisplayWriter
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Save(string path, Vector3 rotation, Vector3 translation, float scale)
    {
        foreach (var value in new[]
                 {
                     rotation.X, rotation.Y, rotation.Z,
                     translation.X, translation.Y, translation.Z, scale,
                 })
        {
            if (!float.IsFinite(value))
                throw new InvalidOperationException($"Refusing to write a non-finite display value to '{path}'.");
        }

        var bytes = File.ReadAllBytes(path);
        var bom = HasUtf8Bom(bytes) ? 3 : 0;
        var json = bytes.AsSpan(bom);

        var reader = new Utf8JsonReader(json, ReaderOptions);
        long valueStart = -1, valueEnd = -1, rootEnd = -1;
        var hasProperties = false;

        while (reader.Read())
        {
            if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndObject)
            {
                rootEnd = reader.BytesConsumed;
                break;
            }

            if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName) continue;

            hasProperties = true;
            if (!Encoding.ASCII.GetString(reader.ValueSpan)
                    .Equals("display", StringComparison.OrdinalIgnoreCase))
                continue;

            reader.Read();
            valueStart = reader.TokenStartIndex;
            reader.Skip();
            valueEnd = reader.BytesConsumed;
        }

        if (rootEnd < 0)
            throw new InvalidOperationException($"'{path}' is not a JSON object; cannot write a display block.");

        var display = Encoding.UTF8.GetBytes(FormatDisplay(rotation, translation, scale));

        byte[] result;
        if (valueStart >= 0)
        {
            result = Concat(
                bytes.AsSpan(0, bom + (int)valueStart).ToArray(),
                display,
                bytes.AsSpan(bom + (int)valueEnd).ToArray());
        }
        else
        {
            var insertAt = bom + (int)rootEnd - 1; // the root object's closing brace
            var separator = Encoding.UTF8.GetBytes(hasProperties ? ",\n  \"display\": " : "\"display\": ");
            result = Concat(
                bytes.AsSpan(0, insertAt).ToArray(),
                separator,
                display,
                "\n"u8.ToArray(),
                bytes.AsSpan(insertAt).ToArray());
        }

        File.WriteAllBytes(path, result);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string FormatDisplay(Vector3 rotation, Vector3 translation, float scale) =>
        $"{{ \"rotation\": [{Number(rotation.X)}, {Number(rotation.Y)}, {Number(rotation.Z)}], "
        + $"\"translation\": [{Number(translation.X)}, {Number(translation.Y)}, {Number(translation.Z)}], "
        + $"\"scale\": {Number(scale)} }}";

    private static string Number(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts) total += part.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
