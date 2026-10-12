using System;
using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// F2's screenshot naming (<see cref="ScreenshotPaths"/>): a timestamped PNG in a folder beside the
/// saves, never colliding within a second.
/// </summary>
public sealed class ScreenshotPathTests
{
    [Fact]
    public void TheFolderSitsBesideTheSaves()
    {
        Assert.Equal("screenshots", Path.GetFileName(ScreenshotPaths.Directory));
        Assert.Equal(Path.GetDirectoryName(WorldStore.DefaultRoot), Path.GetDirectoryName(ScreenshotPaths.Directory));
    }

    [Fact]
    public void TheNameCarriesTheMomentAndNeverCollidesWithinASecond()
    {
        var now = new DateTime(2026, 10, 11, 14, 3, 7, 512);
        var name = ScreenshotPaths.NameFor(now);
        Assert.Matches(@"^screenshot-\d{8}-\d{6}-\d{3}\.png$", name);
        Assert.Contains("20261011-140307-512", name);
        Assert.NotEqual(name, ScreenshotPaths.NameFor(now.AddMilliseconds(1)));
    }
}
