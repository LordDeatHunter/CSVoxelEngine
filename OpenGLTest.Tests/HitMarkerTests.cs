using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The crosshair's hit marker (<see cref="HitMarker"/>): a hit flashes it full, it fades out over
/// its own seconds and a fresh hit restarts the fade.
/// </summary>
public sealed class HitMarkerTests
{
    [Fact]
    public void AFlashFadesOutOverItsSeconds()
    {
        var marker = new HitMarker();
        Assert.Equal(0f, marker.Alpha);

        marker.Show();
        Assert.Equal(1f, marker.Alpha);

        // Half way into the fade it is dimmed but visible
        marker.Update(HitMarker.Seconds * 0.5f);
        Assert.True(marker.Alpha > 0f && marker.Alpha < 1f, $"the flash read {marker.Alpha}");

        // Past it the mark is gone, and a fresh hit restarts it
        marker.Update(HitMarker.Seconds);
        Assert.Equal(0f, marker.Alpha);
        marker.Show();
        Assert.Equal(1f, marker.Alpha);
    }
}
