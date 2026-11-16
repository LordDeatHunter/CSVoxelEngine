using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.DevStudio;

/// <summary>
/// Writes a quest to its <c>&lt;id&gt;.json</c> in the shipped files' hand-written layout: two-space indent, the fields in
/// schema order (<c>title, description, icon, pos, shape, parents, reveal, readable, anyOf, requirements</c>), <c>pos</c>
/// and <c>parents</c> as inline arrays and each requirement as one line. A field at its loader default (no shape, readable
/// true, anyOf false, count 1) is left out, and so is the id, which is the file name. CRLF line endings, UTF-8 without a BOM.
/// </summary>
public static class QuestWriter
{
    private const string NewLine = "\r\n";

    // The relaxed encoder keeps apostrophes and accents readable instead of '-escaping them
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes <paramref name="quest"/> to <paramref name="path"/>.</summary>
    public static void Save(string path, QuestDefinition quest) =>
        File.WriteAllText(path, Format(quest), new UTF8Encoding(false));

    /// <summary>The file's text; two quests format the same exactly when they save the same.</summary>
    public static string Format(QuestDefinition quest)
    {
        var fields = new List<string>();
        if (!string.IsNullOrEmpty(quest.Title)) fields.Add($"\"title\": {Text(quest.Title)}");
        if (!string.IsNullOrEmpty(quest.Description)) fields.Add($"\"description\": {Text(quest.Description)}");
        if (quest.Icon != null) fields.Add($"\"icon\": {Text(quest.Icon)}");
        if (quest.Pos is { } pos) fields.Add($"\"pos\": {Numbers(pos)}");
        if (quest.Shape != null) fields.Add($"\"shape\": {Text(quest.Shape)}");
        if (quest.Parents is { Length: > 0 } parents)
            fields.Add($"\"parents\": [{string.Join(", ", Array.ConvertAll(parents, Text))}]");
        if (quest.Reveal != null) fields.Add($"\"reveal\": {Text(quest.Reveal)}");
        if (quest.Readable == false) fields.Add("\"readable\": false");
        if (quest.AnyOf == true) fields.Add("\"anyOf\": true");

        var requirements = quest.Requirements ?? [];
        var block = new StringBuilder("\"requirements\": [");
        for (var i = 0; i < requirements.Count; i++)
        {
            block.Append(NewLine).Append("    ").Append(FormatRequirement(requirements[i]));
            if (i < requirements.Count - 1) block.Append(',');
        }
        block.Append(requirements.Count > 0 ? NewLine + "  ]" : "]");
        fields.Add(block.ToString());

        var result = new StringBuilder("{");
        for (var i = 0; i < fields.Count; i++)
        {
            result.Append(NewLine).Append("  ").Append(fields[i]);
            if (i < fields.Count - 1) result.Append(',');
        }
        return result.Append(NewLine).Append('}').Append(NewLine).ToString();
    }

    private static string FormatRequirement(QuestRequirementDefinition requirement)
    {
        var fields = new List<string> { $"\"type\": {Text(requirement.Type ?? "")}" };
        if (requirement.Item != null) fields.Add($"\"item\": {Text(requirement.Item)}");
        if (requirement.Tag != null) fields.Add($"\"tag\": {Text(requirement.Tag)}");
        if (requirement.Block != null) fields.Add($"\"block\": {Text(requirement.Block)}");
        if (requirement.Entity != null) fields.Add($"\"entity\": {Text(requirement.Entity)}");
        if (!string.IsNullOrEmpty(requirement.Label)) fields.Add($"\"label\": {Text(requirement.Label)}");
        if (requirement.Count is { } count && count != 1)
            fields.Add($"\"count\": {count.ToString(CultureInfo.InvariantCulture)}");
        return "{ " + string.Join(", ", fields) + " }";
    }

    internal static string Text(string value) => JsonSerializer.Serialize(value, StringOptions);

    // A -0 (a node dragged back onto an axis) writes as 0
    internal static string Numbers(float[] values) =>
        "[" + string.Join(", ", Array.ConvertAll(values,
            v => (v == 0f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture))) + "]";
}
