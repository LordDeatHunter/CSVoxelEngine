using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The per-frame HUD string formatting, memoised (PERF_TODO R8). The FPS line, the compass
/// readout, the coordinate line and every hotbar count used to run a <c>$"..."</c> interpolation
/// or a <c>ToString()</c> on every single frame, allocating a fresh string each time even when the
/// value on screen had not moved; this hands back the previous instance whenever the inputs are
/// unchanged, which is most frames for most of them.
///
/// <para>Three properties the callers' correctness rests on:</para>
/// <list type="bullet">
/// <item><b>Render-thread only, no locking.</b> Every caller is <see cref="UIRenderer"/>, which
/// runs on the render thread (AGENTS.md), so these are plain fields — exactly as
/// <c>Camera</c>'s view-projection cache is (PERF_TODO R7).</item>
/// <item><b>Only the formatting is cached, never the draw.</b> A cache hit skips the
/// interpolation and nothing else: every caller still issues its <c>DrawText</c> /
/// <c>DrawTextCentred</c> call every frame, so the glyph batching of PERF_TODO R9 sees exactly
/// the same submissions it did before and is completely untouched by this class.</item>
/// <item><b>Float keys are compared bit-for-bit</b> through
/// <see cref="BitConverter.SingleToInt32Bits(float)"/>, following <c>Camera</c>'s
/// <c>ViewProjKey</c>, because a hit has to <em>prove</em> the interpolation would have produced
/// the same characters. Plain <c>==</c> is not enough: <c>-0f == 0f</c> is true, but
/// <c>$"{-0f:0.0}"</c> renders <c>"-0.0"</c> while <c>$"{0f:0.0}"</c> renders <c>"0.0"</c>, so an
/// <c>==</c> key would leave the wrong sign on screen for a whole run of frames. Coordinates
/// round to a signed zero whenever the player stands just on the negative side of an axis, so
/// this is a case that really happens, not a theoretical one.</item>
/// </list>
///
/// <para>Each entry uses its own <c>string?</c> field as the "never formatted yet" sentinel, so
/// no value of the key is reserved and the first call for any input is always a miss. The GL half
/// — that the returned string is the one that actually reaches the screen — is not covered by
/// tests here and is only visible by running the game; <c>UITextCacheTests</c> pins the strings
/// against the pre-change inline expressions.</para>
/// </summary>
internal sealed class UITextCache
{
    private string? _fpsText;
    private int _fpsValue;

    private string? _compassText;
    private int _compassHeadingBits;

    private string? _coordsText;
    private int _coordsXBits;
    private int _coordsYBits;
    private int _coordsZBits;

    // Per hotbar slot, so two slots showing the same count keep independent entries and one slot
    // changing never invalidates another. Counts start at 0 and texts at null, and the null is
    // what marks a slot as never formatted — a real count of 0 still formats on its first call.
    private readonly int[] _slotCounts = new int[Inventory.SlotCount];
    private readonly string?[] _slotTexts = new string?[Inventory.SlotCount];

    /// <summary>The FPS line, <c>UIRenderer.DrawFps</c>'s text. Changes about once a second.</summary>
    internal string Fps(int fps)
    {
        if (_fpsText is null || _fpsValue != fps)
        {
            _fpsValue = fps;
            _fpsText = $"FPS {fps}";
        }

        return _fpsText;
    }

    /// <summary>
    /// The compass readout above the tick strip (cardinal plus zero-padded degrees). Keyed on the
    /// heading's bit pattern rather than on the formatted degrees, so it can never be a hit for a
    /// heading whose cardinal name differs.
    /// </summary>
    internal string CompassReadout(float heading)
    {
        var bits = BitConverter.SingleToInt32Bits(heading);
        if (_compassText is null || _compassHeadingBits != bits)
        {
            _compassHeadingBits = bits;
            _compassText = $"{Compass.CardinalName(heading)} {(int)MathF.Round(heading) % 360:D3}";
        }

        return _compassText;
    }

    /// <summary>
    /// The XYZ line under the compass. Keyed on all three component bit patterns: any component
    /// moving at all is a miss, which is conservative (the rendered text only changes on the first
    /// decimal) but keeps the hit a proof rather than a guess.
    /// </summary>
    internal string Coords(Vector3 position)
    {
        var xBits = BitConverter.SingleToInt32Bits(position.X);
        var yBits = BitConverter.SingleToInt32Bits(position.Y);
        var zBits = BitConverter.SingleToInt32Bits(position.Z);

        if (_coordsText is null || _coordsXBits != xBits || _coordsYBits != yBits || _coordsZBits != zBits)
        {
            _coordsXBits = xBits;
            _coordsYBits = yBits;
            _coordsZBits = zBits;
            _coordsText = $"XYZ {position.X:0.0} / {position.Y:0.0} / {position.Z:0.0}";
        }

        return _coordsText;
    }

    /// <summary>
    /// The count drawn in hotbar slot <paramref name="slot"/>'s corner. Cached per slot, because
    /// the hotbar formats up to <see cref="Inventory.SlotCount"/> of these every frame and they
    /// only move on a pickup or a place.
    /// </summary>
    internal string SlotCount(int slot, int count)
    {
        if (_slotTexts[slot] is null || _slotCounts[slot] != count)
        {
            _slotCounts[slot] = count;
            _slotTexts[slot] = count.ToString();
        }

        return _slotTexts[slot]!;
    }
}
