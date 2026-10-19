using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace VoxelEngine.App;

/// <summary>The developer diagnostics: the startup build line, the VOXEL_TIME read, the live debug flag (the X key, VOXEL_DEBUG) and the X debug line it gates. Owned by Program; main thread only.</summary>
internal sealed class DevDiagnostics
{
    // Set to a number of seconds to jump the day/night cycle at startup
    private const string TimeOffsetVariable = "VOXEL_TIME";

    // Set to 1 to start with the X debug line on, so a scripted run logs FPS and the profiler
    // breakdown once a second without anyone pressing a key
    internal const string DebugVariable = "VOXEL_DEBUG";

    private bool debugEnabled;

    /// <summary>The live X debug state, read by the GPU timer gate, the HUD and the network log.</summary>
    internal bool DebugEnabled { get => debugEnabled; set => debugEnabled = value; }

    private double debugLogTimer;

    /// <summary>
    /// One line naming the build the timings were taken on. A Debug build, an attached
    /// debugger and workstation GC each cost enough that a measurement without them recorded
    /// cannot be compared against anything.
    /// </summary>
    internal static void PrintBuildDiagnostics()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        Log.WriteLine(
            $"Build: {configuration} | debugger {Debugger.IsAttached} | serverGC {GCSettings.IsServerGC}"
            + $" | latency {GCSettings.LatencyMode} | cpus {Environment.ProcessorCount}");
    }

    /// <summary>
    /// Dev override: VOXEL_TIME="seconds" offsets the clock the lighting pass reads, so noon,
    /// dusk or night can be photographed straight away instead of after minutes of orbit.
    /// Parsed once here; an unparseable value is ignored with a warning, like VOXEL_SPAWN.
    /// </summary>
    internal static double ReadTimeOffset()
    {
        var timeOverride = Environment.GetEnvironmentVariable(TimeOffsetVariable);
        if (string.IsNullOrWhiteSpace(timeOverride))
            return 0.0;

        if (double.TryParse(timeOverride.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var offset))
            return offset;

        Log.WriteLine(
            $"Ignoring {TimeOffsetVariable}=\"{timeOverride}\": expected a number of seconds, e.g. 150");
        return 0.0;
    }

    /// <summary>
    /// The X debug line: FPS and camera state five times a second plus one profiler snapshot a
    /// second. Shared by the interactive and the scripted update paths, so a VOXEL_DEBUG run logs
    /// the same numbers a player would see.
    /// </summary>
    internal void LogDebugLine(double deltaTime, World? world)
    {
        if (!debugEnabled) return;

        if (FrameProfiler.TryTakeSnapshotForConsole(out var profilerLine))
            Log.WriteLine(profilerLine);

        debugLogTimer += deltaTime;
        if (debugLogTimer < 0.2) return;

        // The camera line needs a world; the profiler snapshot above prints on every screen
        if (world == null)
        {
            debugLogTimer = 0;
            return;
        }

        var fps = deltaTime > 0 ? (int)(1 / deltaTime) : 0;
        var camera = world.Camera;
        var position = camera.Position;
        Log.WriteLine(
            $"FPS {fps} | Pos ({position.X:F2}, {position.Y:F2}, {position.Z:F2}) | Grounded {camera.IsGrounded} | Vy {camera.VerticalVelocity:F2} | Water {camera.IsInWater}/{camera.IsEyeInWater} | Move {camera.MovementFactor:F2} | Fly {camera.IsFlying}");
        debugLogTimer = 0;
    }
}
