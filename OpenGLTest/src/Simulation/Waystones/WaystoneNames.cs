using System;
using System.Text;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// Waystone names, GL-free: a fresh waystone gets a completely random name by the Fabric Waystones mod's rule (four
/// consonant + vowel pairs, the first letter capitalized, e.g. "Kafuziro"; the mod's easter egg is left out), and a
/// rename is cleaned by <see cref="TrySanitize"/> before it is stored.
/// </summary>
public static class WaystoneNames
{
    /// <summary>The longest name a waystone keeps (a rename is cut to it).</summary>
    public const int MaxLength = 24;

    /// <summary>Consonant-vowel pairs in a generated name.</summary>
    public const int Pairs = 4;

    /// <summary>The 21 consonants a generated name draws from.</summary>
    public const string Consonants = "bcdfghjklmnpqrstvwxyz";

    /// <summary>The 5 vowels a generated name draws from.</summary>
    public const string Vowels = "aeiou";

    /// <summary>A random name drawn from <paramref name="rng"/>: <see cref="Pairs"/> consonant-vowel pairs, the
    /// first letter upper case.</summary>
    public static string Generate(Random rng)
    {
        var sb = new StringBuilder(Pairs * 2);
        for (var i = 0; i < Pairs; i++)
        {
            var consonant = Consonants[rng.Next(Consonants.Length)];
            sb.Append(i == 0 ? char.ToUpperInvariant(consonant) : consonant);
            sb.Append(Vowels[rng.Next(Vowels.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A rename (or a saved name) as a waystone keeps it: printable ASCII only (anything else is dropped), runs of
    /// spaces collapsed, trimmed and cut to <see cref="MaxLength"/>. False, with an empty name, when nothing is
    /// left, so a blank rename is refused rather than stored.
    /// </summary>
    public static bool TrySanitize(string? raw, out string name)
    {
        name = "";
        if (raw == null) return false;
        var sb = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (var c in raw)
        {
            if (c is < ' ' or > '~') continue;
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            if (sb.Length == MaxLength) break;
            sb.Append(c);
        }
        name = sb.ToString().TrimEnd();
        return name.Length > 0;
    }
}
