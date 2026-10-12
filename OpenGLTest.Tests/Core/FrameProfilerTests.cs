using System;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class FrameProfilerTests
{
    [Fact]
    public void SectionsAndLabelsStayInSync()
    {
        Assert.Equal(Enum.GetValues<ProfileSection>().Length, FrameProfiler.SectionCount);
    }

    [Fact]
    public void EverySectionTimesWithoutThrow()
    {
        foreach (var section in Enum.GetValues<ProfileSection>())
        {
            FrameProfiler.Begin(section);
            FrameProfiler.End(section);
        }
        FrameProfiler.EndFrame();
    }
}
