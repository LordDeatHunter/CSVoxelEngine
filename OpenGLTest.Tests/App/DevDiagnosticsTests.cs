using System;
using System.Collections.Generic;
using VoxelEngine.App;
using VoxelEngine.Core;
using Xunit;

namespace VoxelEngine.Tests.App;

public sealed class DevDiagnosticsTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void NonFiniteTimeOffsetsFallBackToZero(string raw)
    {
        var offset = DevDiagnostics.ParseTimeOffset(raw);

        Assert.Equal(0.0, offset);
        Assert.True(double.IsFinite(offset));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("not-a-number")]
    public void MissingOrMalformedTimeOffsetsFallBackToZero(string? raw)
    {
        Assert.Equal(0.0, DevDiagnostics.ParseTimeOffset(raw));
    }

    [Theory]
    [InlineData("  +147.5  ", 147.5)]
    [InlineData("-2.5e2", -250.0)]
    [InlineData("1e300", 1e300)]
    public void FiniteTimeOffsetsArePreserved(string raw, double expected)
    {
        Assert.Equal(expected, DevDiagnostics.ParseTimeOffset(raw));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void TimeOverrideAloneDoesNotBypassMenu(string raw)
    {
        var values = new Dictionary<string, string> { ["VOXEL_TIME"] = raw };
        string? Read(string name) => values.TryGetValue(name, out var value) ? value : null;

        Assert.False(LaunchMode.BypassesMenu(Read));
    }
}
