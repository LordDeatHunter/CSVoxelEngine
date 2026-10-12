using System;
using System.Collections.Generic;
using System.Text.Json;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking;

/// <summary>
/// The players a dedicated server lets be creative even while it does not allow creative for
/// everyone (<see cref="GameServer.MayBeCreative"/>). GL-free and content-free.
/// <para>
/// Names are matched by their save slug (<see cref="WorldNames.Slug"/>), the same normalisation
/// that keys a player's save file, so "Alice", "alice" and "ALICE!" are one operator, exactly as
/// they are one save. The list comes from two places, merged with <see cref="AddAll"/>: every
/// <c>--op &lt;name&gt;</c> on the server's command line (<see cref="FromArgs"/>) and a JSON array of
/// names in the hosted world's folder (<see cref="FileName"/>, <see cref="Parse"/>). With neither,
/// nobody is an operator. Players are known only by the name they join with, so the list grants
/// creative to whoever joins under a listed name first.
/// </para>
/// </summary>
public sealed class OperatorList
{
    /// <summary>The operator file in a hosted world's folder: a JSON array of player names.</summary>
    public const string FileName = "ops.json";

    private const string OpFlag = "--op";

    private readonly HashSet<string> _slugs = new(StringComparer.Ordinal);
    // The names as first given, in order, for the server's startup line
    private readonly List<string> _names = [];

    /// <summary>How many distinct operators (by slug) the list holds.</summary>
    public int Count => _slugs.Count;

    /// <summary>The operators' names as first given, one per slug, in the order they were added.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>Adds <paramref name="name"/>; a blank name, or one whose slug is already listed, adds nothing.</summary>
    public void Add(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;
        if (_slugs.Add(WorldNames.Slug(trimmed)))
            _names.Add(trimmed);
    }

    /// <summary>Whether a player joining as <paramref name="name"/> is an operator (slug match).</summary>
    public bool Contains(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && _slugs.Contains(WorldNames.Slug(trimmed));
    }

    /// <summary>Adds every operator of <paramref name="other"/> to this list.</summary>
    public void AddAll(OperatorList other)
    {
        foreach (var name in other._names)
            Add(name);
    }

    /// <summary>
    /// The operators an <see cref="FileName"/> file lists: a JSON array of strings (comments and
    /// trailing commas tolerated, a UTF-8 byte-order mark skipped, entries that are not strings
    /// skipped). No file (<paramref name="json"/> null) is an empty list; an empty or unparseable
    /// file, or one whose root is not an array, is an empty list with a warning naming the file.
    /// </summary>
    public static OperatorList Parse(byte[]? json)
    {
        var list = new OperatorList();
        if (json == null)
            return list;

        // A hand-edited file saved by a Windows editor may start with the UTF-8 byte-order mark,
        // which the JSON reader refuses
        ReadOnlyMemory<byte> bytes = json;
        if (bytes.Span.StartsWith("﻿"u8))
            bytes = bytes[3..];

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log.Warn($"Operators: ignoring {FileName}: expected a JSON array of player names");
                return list;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                    list.Add(entry.GetString());
            }
        }
        catch (JsonException e)
        {
            Log.Warn($"Operators: ignoring {FileName}: {e.Message}");
        }
        return list;
    }

    /// <summary>
    /// The operators named on a command line: the argument after every <c>--op</c> (the flag
    /// matched case-insensitively). An <c>--op</c> with no name after it (the last argument, or
    /// another <c>--</c> flag) names nobody and is warned about.
    /// </summary>
    public static OperatorList FromArgs(string[] args)
    {
        var list = new OperatorList();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(OpFlag, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Log.Warn($"Operators: {OpFlag} needs a player name after it");
                continue;
            }

            list.Add(args[++i]);
        }
        return list;
    }
}
