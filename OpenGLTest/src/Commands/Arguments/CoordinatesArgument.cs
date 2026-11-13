using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace VoxelEngine.Commands.Arguments;

/// <summary>
/// A position written as three tokens <c>x y z</c> (a <see cref="Vector3"/>): each a number, or <c>~</c> /
/// <c>~offset</c> relative to the source's <see cref="CommandSource.Feet"/> on that axis. Completes <c>~</c>, the
/// source's own block coordinate on the axis being typed and, on the first, the whole <c>~ ~ ~</c> and
/// <c>x y z</c> of where it stands.
/// </summary>
public sealed class CoordinatesArgument : IArgumentType
{
    public int TokenCount => 3;

    public string Syntax(string name) => "<x y z>";

    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        Span<float> axes = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            if (TryAxis(tokens[i], Axis(source.Feet, i), out axes[i])) continue;
            value = Vector3.Zero;
            error = $"'{tokens[i]}' is not a coordinate (a number, ~ or ~offset)";
            return false;
        }
        value = new Vector3(axes[0], axes[1], axes[2]);
        error = "";
        return true;
    }

    public bool CouldStart(ReadOnlySpan<string> tokens)
    {
        foreach (var token in tokens)
            if (!TryAxis(token, 0f, out _))
                return false;
        return true;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        into.Add(new Suggestion("~"));
        var block = Block(Axis(source.Feet, tokenIndex));
        into.Add(new Suggestion(block));
        if (tokenIndex != 0) return;
        into.Add(new Suggestion("~ ~ ~"));
        into.Add(new Suggestion($"{block} {Block(source.Feet.Y)} {Block(source.Feet.Z)}"));
    }

    private static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static string Block(float coordinate) =>
        ((int)MathF.Floor(coordinate)).ToString(CultureInfo.InvariantCulture);

    private static bool TryAxis(string token, float origin, out float value)
    {
        var relative = token.StartsWith('~');
        var number = relative ? token[1..] : token;
        if (relative && number.Length == 0)
        {
            value = origin;
            return true;
        }
        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !float.IsFinite(parsed))
        {
            value = 0f;
            return false;
        }
        value = relative ? origin + parsed : parsed;
        return true;
    }
}
