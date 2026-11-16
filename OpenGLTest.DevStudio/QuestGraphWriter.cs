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
/// Writes a <see cref="QuestGraphDocument"/> back to its JSON file in the shipped files' hand-written layout:
/// two-space indent, the header comment kept under the opening brace, each quest's fields in schema order
/// (<c>id, title, description, icon, pos, shape, parents, readable, anyOf, requirements</c>), <c>pos</c>,
/// <c>origin</c> and <c>parents</c> as inline arrays and each requirement as one line. A field at its loader
/// default (no shape, readable true, anyOf false, count 1) is left out. CRLF line endings, UTF-8 without a BOM.
/// </summary>
public static class QuestGraphWriter
{
    private const string NewLine = "\r\n";

    // The relaxed encoder keeps apostrophes and accents readable instead of '-escaping them
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Save(QuestGraphDocument document) => Save(document.Path, document.Definition, document.Header);

    /// <summary>Writes <paramref name="graph"/> with the <paramref name="header"/> comment lines to <paramref name="path"/>.</summary>
    public static void Save(string path, QuestGraphDefinition graph, IReadOnlyList<string> header) =>
        File.WriteAllText(path, Format(graph, header), new UTF8Encoding(false));

    public static string Format(QuestGraphDocument document) => Format(document.Definition, document.Header);

    public static string Format(QuestGraphDefinition graph, IReadOnlyList<string> header)
    {
        var lines = new List<string> { "{" };
        foreach (var comment in header) lines.Add("  " + comment);

        var top = new List<string>();
        if (!string.IsNullOrEmpty(graph.Title)) top.Add($"  \"title\": {Text(graph.Title)}");
        if (graph.Origin is { } origin) top.Add($"  \"origin\": {Numbers(origin)}");
        var quests = graph.Quests ?? [];
        var questBlock = new StringBuilder("  \"quests\": [");
        for (var i = 0; i < quests.Count; i++)
        {
            questBlock.Append(NewLine).Append(FormatQuest(quests[i]));
            if (i < quests.Count - 1) questBlock.Append(',');
        }
        questBlock.Append(quests.Count > 0 ? NewLine + "  ]" : "]");
        top.Add(questBlock.ToString());

        for (var i = 0; i < top.Count; i++)
            lines.Add(i < top.Count - 1 ? top[i] + "," : top[i]);
        lines.Add("}");
        return string.Join(NewLine, lines) + NewLine;
    }

    /// <summary>One quest's block, as it sits in the file; two quests format the same exactly when they save the same.</summary>
    internal static string FormatQuest(QuestGraphDefinition.QuestDefinition quest)
    {
        var fields = new List<string> { $"\"id\": {Text(quest.Id ?? "")}" };
        if (!string.IsNullOrEmpty(quest.Title)) fields.Add($"\"title\": {Text(quest.Title)}");
        if (!string.IsNullOrEmpty(quest.Description)) fields.Add($"\"description\": {Text(quest.Description)}");
        if (quest.Icon != null) fields.Add($"\"icon\": {Text(quest.Icon)}");
        if (quest.Pos is { } pos) fields.Add($"\"pos\": {Numbers(pos)}");
        if (quest.Shape != null) fields.Add($"\"shape\": {Text(quest.Shape)}");
        if (quest.Parents is { Length: > 0 } parents)
            fields.Add($"\"parents\": [{string.Join(", ", Array.ConvertAll(parents, Text))}]");
        if (quest.Readable == false) fields.Add("\"readable\": false");
        if (quest.AnyOf == true) fields.Add("\"anyOf\": true");

        var requirements = quest.Requirements ?? [];
        var block = new StringBuilder("\"requirements\": [");
        for (var i = 0; i < requirements.Count; i++)
        {
            block.Append(NewLine).Append("        ").Append(FormatRequirement(requirements[i]));
            if (i < requirements.Count - 1) block.Append(',');
        }
        block.Append(requirements.Count > 0 ? NewLine + "      ]" : "]");
        fields.Add(block.ToString());

        var result = new StringBuilder("    {");
        for (var i = 0; i < fields.Count; i++)
        {
            result.Append(NewLine).Append("      ").Append(fields[i]);
            if (i < fields.Count - 1) result.Append(',');
        }
        return result.Append(NewLine).Append("    }").ToString();
    }

    private static string FormatRequirement(QuestGraphDefinition.RequirementDefinition requirement)
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

    private static string Text(string value) => JsonSerializer.Serialize(value, StringOptions);

    // A -0 (a node dragged back onto an axis) writes as 0
    private static string Numbers(float[] values) =>
        "[" + string.Join(", ", Array.ConvertAll(values,
            v => (v == 0f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture))) + "]";
}
