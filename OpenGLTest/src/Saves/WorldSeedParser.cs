using System;
using System.Globalization;

namespace silkgltriangle;

/// <summary>
/// GL-free rule turning the create-world seed field into a world seed. Empty or whitespace text
/// draws a seed from the supplied random provider; text that parses as an <see cref="int"/>
/// (invariant culture, negatives allowed, surrounding whitespace ignored) is used verbatim; any
/// other text is hashed with 32-bit FNV-1a over its UTF-16 code units, which is stable across
/// processes and runtimes (unlike <see cref="string.GetHashCode()"/>) and case-sensitive.
/// </summary>
public static class WorldSeedParser
{
    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    public static int Parse(string? text, Func<int> random)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return random();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;

        return unchecked((int)Fnv1a(trimmed));
    }

    /// <summary>
    /// 32-bit FNV-1a over the UTF-16 code units of <paramref name="text"/>: stable across
    /// processes and runtimes and case-sensitive. Shared with the tree grower's species salt.
    /// </summary>
    internal static uint Fnv1a(string text)
    {
        var hash = FnvOffsetBasis;
        unchecked
        {
            foreach (var c in text)
            {
                hash ^= c;
                hash *= FnvPrime;
            }
        }
        return hash;
    }
}
