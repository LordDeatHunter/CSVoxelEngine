using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace silkgltriangle;

/// <summary>
/// GL-free, content-free rules for world display names and their save-folder slugs.
/// <para>
/// A requested name is trimmed, falls back to <see cref="DefaultName"/> when empty or
/// whitespace, and is truncated to <see cref="MaxNameLength"/>. Display names are unique
/// case-insensitively: a taken name gets the smallest free numeric suffix starting at 2
/// ("Name 2", "Name 3", ...), so a gap left by a deleted world is filled before going higher.
/// </para>
/// <para>
/// The folder slug is derived once, at creation, and never follows a rename: lower-case ASCII
/// letters and digits are kept, every other run of characters becomes a single '-', leading and
/// trailing dashes are trimmed, an empty result becomes "world", and the slug is capped at
/// <see cref="MaxSlugLength"/>. That makes any slug a single, valid folder name on every OS.
/// </para>
/// </summary>
public static class WorldNames
{
    public const string DefaultName = "Unnamed World";
    public const int MaxNameLength = 32;
    public const int MaxSlugLength = 48;
    private const string DefaultSlug = "world";

    /// <summary>Trims the request, substitutes the default for an empty one and caps its length.</summary>
    public static string Normalise(string? requested)
    {
        var name = requested?.Trim();
        if (string.IsNullOrEmpty(name))
            return DefaultName;
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength].TrimEnd();
        return name;
    }

    /// <summary>
    /// The normalised request when no existing name matches it case-insensitively, otherwise
    /// "<c>name n</c>" for the smallest n &gt;= 2 that is free. The numbered form may exceed
    /// <see cref="MaxNameLength"/>.
    /// </summary>
    public static string Unique(string? requested, IEnumerable<string> existing)
    {
        var name = Normalise(requested);
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>A filesystem-safe folder name for <paramref name="name"/> (see the class doc).</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        var lastWasDash = false;
        foreach (var raw in name.ToLowerInvariant())
        {
            if (raw is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(raw);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength].TrimEnd('-');
        if (slug.Length == 0)
            return DefaultSlug;
        // "con", "nul", "com1"... resolve to devices on Windows, so a folder of that name can never
        // be created; the suffix keeps the slug a plain folder name.
        return IsReservedDeviceName(slug) ? slug + ReservedSuffix : slug;
    }

    private const string ReservedSuffix = "-world";

    /// <summary>Whether <paramref name="slug"/> is a Windows reserved device name (case-insensitive).</summary>
    public static bool IsReservedDeviceName(string slug)
    {
        if (slug.Length is 3 && (slug.Equals("con", StringComparison.OrdinalIgnoreCase)
            || slug.Equals("prn", StringComparison.OrdinalIgnoreCase)
            || slug.Equals("aux", StringComparison.OrdinalIgnoreCase)
            || slug.Equals("nul", StringComparison.OrdinalIgnoreCase)))
            return true;
        return slug.Length == 4 && slug[3] is >= '0' and <= '9'
            && (slug.StartsWith("com", StringComparison.OrdinalIgnoreCase)
                || slug.StartsWith("lpt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <see cref="Slug"/> of the name, or "<c>slug-n</c>" for the smallest n &gt;= 2 that
    /// <paramref name="exists"/> rejects.
    /// </summary>
    public static string UniqueSlug(string name, Func<string, bool> exists)
    {
        var slug = Slug(name);
        if (!exists(slug))
            return slug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!exists(candidate))
                return candidate;
        }
    }
}
