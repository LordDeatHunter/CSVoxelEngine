using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>UITextCache</c> is the GL-free half of the HUD's per-frame text (PERF_TODO R8): the four
/// string sites <see cref="UIRenderer"/> used to re-format every frame. It lives outside
/// <c>UIRenderer</c> for the same reason <c>TextureUnitCache</c> and <c>HalfResTarget</c> do —
/// <c>UIRenderer</c>'s constructor needs a live GL context and loads shaders, so the only way to
/// pin this arithmetic headlessly is to keep it out here.
///
/// <para>Each fact carries a private <c>Reference*</c> oracle that re-expresses the pre-change
/// inline expression from <c>UIRenderer</c> verbatim, and asserts exact string equality against
/// it. Those oracles are the checked-in proof that the cache hands back the characters the old
/// interpolation produced; if a format string is ever edited in <c>UIRenderer</c> without editing
/// it here, these facts are the thing that has to be consciously updated.</para>
///
/// <para>What is NOT covered, and is only visible by running the game: the <c>DrawText</c> /
/// <c>DrawTextCentred</c> calls themselves. A cache hit is supposed to skip the formatting and
/// nothing else — every caller still draws every frame — but nothing here can observe a draw, and
/// nothing here can observe the <em>absence</em> of an allocation either. These facts pin the
/// strings; the screenshot gate and an eyeball read of the HUD pin the pixels.</para>
/// </summary>
public sealed class UITextCacheTests
{
    // The four pre-change expressions, copied character-for-character from UIRenderer's
    // DrawFps (:381), DrawCompass readout (:434) and coords (:472), and DrawHotbar (:648).
    private static string ReferenceFps(int fps) => $"FPS {fps}";

    private static string ReferenceReadout(float heading) =>
        $"{Compass.CardinalName(heading)} {(int)MathF.Round(heading) % 360:D3}";

    private static string ReferenceCoords(Vector3 position) =>
        $"XYZ {position.X:0.0} / {position.Y:0.0} / {position.Z:0.0}";

    private static string ReferenceSlotCount(int count) => count.ToString();

    /// <summary>
    /// Every FPS value the HUD can show formats exactly as the inline interpolation did, on a
    /// fresh entry and again on a warm one.
    /// </summary>
    [Fact]
    public void FpsTextMatchesTheInlineFormat()
    {
        var cache = new UITextCache();
        int[] values = [0, 1, 9, 60, 144, 1000, int.MaxValue];

        var distinct = new HashSet<string>();
        foreach (var fps in values)
        {
            Assert.Equal(ReferenceFps(fps), cache.Fps(fps));
            // Again on a warm entry: a hit must return the same characters, not just the
            // first miss.
            Assert.Equal(ReferenceFps(fps), cache.Fps(fps));
            distinct.Add(cache.Fps(fps));
        }

        // Not vacuous: the sweep really did exercise several different texts.
        Assert.Equal(values.Length, distinct.Count);
    }

    /// <summary>
    /// The point of the class: an unchanged value hands back the very same instance, and a
    /// changed one formats afresh. Asserted for all four members.
    ///
    /// Each pair is probed hit-then-miss and never re-probed afterwards, because a miss
    /// <em>replaces</em> the cached entry — asking for the old value again after a miss is
    /// legitimately a fresh format, not a bug.
    ///
    /// The slot-count half deliberately uses counts above 299. The BCL hands back a shared,
    /// cached instance from <c>int.ToString()</c> for values 0..299 (measured on this runtime),
    /// so a reference comparison on a smaller count would pass whether or not this class cached
    /// anything — a vacuous assertion about the runtime rather than about us. Counts that large
    /// are ordinary here: a block pool holds up to <see cref="Inventory.BlockCapacity"/>.
    /// </summary>
    [Fact]
    public void RepeatedValuesReturnTheSameInstance()
    {
        var cache = new UITextCache();
        var hits = 0;
        var misses = 0;

        var fpsA = cache.Fps(60);
        var fpsHit = cache.Fps(60);
        var fpsMiss = cache.Fps(61);
        if (ReferenceEquals(fpsA, fpsHit)) hits++;
        if (!ReferenceEquals(fpsA, fpsMiss)) misses++;
        Assert.Same(fpsA, fpsHit);
        Assert.NotSame(fpsA, fpsMiss);

        var headingA = cache.CompassReadout(12.5f);
        var headingHit = cache.CompassReadout(12.5f);
        var headingMiss = cache.CompassReadout(200.25f);
        if (ReferenceEquals(headingA, headingHit)) hits++;
        if (!ReferenceEquals(headingA, headingMiss)) misses++;
        Assert.Same(headingA, headingHit);
        Assert.NotSame(headingA, headingMiss);

        var posA = new Vector3(1.25f, 70.5f, -3.75f);
        var posB = new Vector3(1.25f, 70.5f, -3.25f);
        var coordsA = cache.Coords(posA);
        var coordsHit = cache.Coords(posA);
        var coordsMiss = cache.Coords(posB);
        if (ReferenceEquals(coordsA, coordsHit)) hits++;
        if (!ReferenceEquals(coordsA, coordsMiss)) misses++;
        Assert.Same(coordsA, coordsHit);
        Assert.NotSame(coordsA, coordsMiss);

        var slotA = cache.SlotCount(3, 512);
        var slotHit = cache.SlotCount(3, 512);
        var slotMiss = cache.SlotCount(3, 513);
        if (ReferenceEquals(slotA, slotHit)) hits++;
        if (!ReferenceEquals(slotA, slotMiss)) misses++;
        Assert.Same(slotA, slotHit);
        Assert.NotSame(slotA, slotMiss);

        // Both halves of the cache actually ran: four hits and four misses, so the fact cannot
        // pass by never caching or by never re-formatting.
        Assert.Equal(4, hits);
        Assert.Equal(4, misses);
    }

    /// <summary>
    /// The compass readout over a full turn at the resolution the HUD moves through, plus the
    /// exact half-cardinal boundaries where <see cref="Compass.CardinalName"/> flips and a value
    /// that rounds up into the 360 wrap.
    /// </summary>
    [Fact]
    public void CompassReadoutMatchesTheInlineFormat()
    {
        var cache = new UITextCache();
        var distinct = new HashSet<string>();

        for (var step = 0; step <= 719; step++)
        {
            var heading = step * 0.5f;
            Assert.Equal(ReferenceReadout(heading), cache.CompassReadout(heading));
            distinct.Add(cache.CompassReadout(heading));
        }

        foreach (var heading in new[] { 22.5f, 67.5f, 359.9f })
        {
            Assert.Equal(ReferenceReadout(heading), cache.CompassReadout(heading));
            distinct.Add(cache.CompassReadout(heading));
        }

        // Not vacuous: the sweep covered every whole degree of the readout (0.5 rounds to even,
        // so 720 half-degree steps land on 360 distinct texts), and a cache that returned one
        // stale string would have been caught hundreds of times over.
        Assert.Equal(360, distinct.Count);
    }

    /// <summary>
    /// The coordinate line, including negative components, magnitudes far from spawn, and the
    /// case that motivates the bit-for-bit key: a component that rounds to a signed zero.
    /// </summary>
    [Fact]
    public void CoordsMatchTheInlineFormat()
    {
        var cache = new UITextCache();
        Vector3[] positions =
        [
            new(0f, 0f, 0f),
            new(0.5f, 64.25f, 0.5f),
            new(-1.5f, 70f, -2.5f),
            new(-0.04f, 63.96f, -0.04f),
            new(12345.678f, 255.5f, -98765.43f),
            new(-0.0001f, -0.0001f, -0.0001f),
            new(1e7f, -1e7f, 1e-7f),
        ];

        var distinct = new HashSet<string>();
        foreach (var position in positions)
        {
            Assert.Equal(ReferenceCoords(position), cache.Coords(position));
            // Again on the warm entry.
            Assert.Equal(ReferenceCoords(position), cache.Coords(position));
            distinct.Add(cache.Coords(position));
        }

        // Not vacuous, and the sweep really did reach the signed-zero rendering the bit key
        // exists for.
        Assert.Equal(positions.Length, distinct.Count);
        Assert.Contains("-0.0", ReferenceCoords(new Vector3(-0.04f, 63.96f, -0.04f)));
    }

    /// <summary>
    /// The fact that justifies keying on <c>BitConverter.SingleToInt32Bits</c> instead of on
    /// <c>==</c>. <c>-0f == 0f</c> is true, so an <c>==</c> key would treat a step onto the
    /// negative side of an axis as a cache hit and leave <c>"0.0"</c> on screen where the inline
    /// interpolation would have written <c>"-0.0"</c>.
    ///
    /// The compass is checked too, but honestly: its format rounds the heading to an <c>int</c>
    /// and prints it with <c>:D3</c>, and an integer zero carries no sign, so <c>+0f</c> and
    /// <c>-0f</c> legitimately produce equal text there. That half asserts the cache still agrees
    /// with its oracle rather than asserting a difference that does not exist. The load-bearing
    /// half is <c>Coords</c>.
    /// </summary>
    [Fact]
    public void NegativeZeroDoesNotReuseThePositiveZeroText()
    {
        var cache = new UITextCache();

        var positive = new Vector3(0f, 0f, 0f);
        var negative = new Vector3(-0f, -0f, -0f);

        var positiveText = cache.Coords(positive);
        Assert.Equal(ReferenceCoords(positive), positiveText);

        var negativeText = cache.Coords(negative);
        Assert.Equal(ReferenceCoords(negative), negativeText);

        // The whole point: equal under ==, different characters on screen.
        Assert.Equal(positive, negative);
        Assert.NotEqual(positiveText, negativeText);
        Assert.Equal("XYZ 0.0 / 0.0 / 0.0", positiveText);
        Assert.Equal("XYZ -0.0 / -0.0 / -0.0", negativeText);

        // And back again, to catch a key that only updates in one direction.
        Assert.Equal(ReferenceCoords(positive), cache.Coords(positive));

        // The compass: -0f is a miss on the bit key but formats identically, which is correct.
        var compassPositive = cache.CompassReadout(0f);
        var compassNegative = cache.CompassReadout(-0f);
        Assert.Equal(ReferenceReadout(0f), compassPositive);
        Assert.Equal(ReferenceReadout(-0f), compassNegative);
        Assert.Equal(compassPositive, compassNegative);
    }

    /// <summary>
    /// Slot counts are cached per slot, so two slots holding the same count keep independent
    /// entries and changing one never disturbs another. Counts above 299 carry the instance
    /// assertions, for the BCL-cache reason noted on
    /// <see cref="RepeatedValuesReturnTheSameInstance"/>.
    /// </summary>
    [Fact]
    public void SlotCountsAreCachedPerSlot()
    {
        var cache = new UITextCache();

        var slot2 = cache.SlotCount(2, 512);
        var slot5 = cache.SlotCount(5, 512);
        Assert.Equal(ReferenceSlotCount(512), slot2);
        Assert.Equal(ReferenceSlotCount(512), slot5);

        // Same characters, but two independent entries: slot 5 did not read slot 2's.
        Assert.Equal(slot2, slot5);
        Assert.NotSame(slot2, slot5);

        // Changing slot 2 leaves slot 5 exactly as it was, instance included.
        var slot2Changed = cache.SlotCount(2, 511);
        Assert.Equal(ReferenceSlotCount(511), slot2Changed);
        Assert.Same(slot5, cache.SlotCount(5, 512));
        Assert.Same(slot2Changed, cache.SlotCount(2, 511));

        // Every slot the hotbar can draw is addressable, and a first call for a slot formats even
        // when the count matches the backing array's zero default.
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            Assert.Equal(ReferenceSlotCount(0), cache.SlotCount(slot, 0));

        for (var slot = 0; slot < Inventory.SlotCount; slot++)
        {
            var count = slot * 7 + 11;
            Assert.Equal(ReferenceSlotCount(count), cache.SlotCount(slot, count));
        }

        // And they did not overwrite one another on the way through.
        for (var slot = 0; slot < Inventory.SlotCount; slot++)
            Assert.Equal(ReferenceSlotCount(slot * 7 + 11), cache.SlotCount(slot, slot * 7 + 11));
    }
}
