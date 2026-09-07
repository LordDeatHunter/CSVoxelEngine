using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace silkgltriangle;

public static class Program
{
    private static WindowManager windowManager;
    private static InputManager inputManager;
    private static RenderManager renderManager;
    private static World world;
    private static VisualDirector? visualDirector;
    private static bool visualScriptFailed;
    private static bool debugEnabled;
    private static bool xWasPressed;
    private static bool qWasPressed;
    private static double debugLogTimer;
    private static double fpsTimer;
    private static int fpsFrames;
    private static int shownFps;
    // TODO: move this to player class
    private static double placeTimer = MAX_PLACE_TIMER;
    private static readonly double MAX_PLACE_TIMER = 0.2;

    // Set to a number of seconds to jump the day/night cycle at startup
    private const string TimeOffsetVariable = "VOXEL_TIME";

    // Set to a scenario JSON path to run a scripted visual test instead of playing:
    // teleport/walk/mine/screenshot steps drive the real update functions, then the
    // window closes. A bad path or bad script fails fast instead of trapping anyone.
    private const string VisualScriptVariable = "VOXEL_SCRIPT";

    // Added to the window clock before the lighting pass sees it, so a screenshot can be taken
    // at any point of the day without waiting for the sun to get there
    private static double timeOffset;

    public static void Main()
    {
        windowManager = new WindowManager();

        windowManager.Window.Load += OnLoad;
        windowManager.Window.Update += OnUpdate;
        windowManager.Window.Render += OnRender;
        windowManager.Window.Closing += OnClosing;
        windowManager.Window.FramebufferResize += OnFramebufferResize;

        windowManager.Run();
    }

    private static void OnLoad()
    {
        PrintBuildDiagnostics();
        timeOffset = ReadTimeOffset();

        inputManager = new InputManager(windowManager.Window);
        renderManager = new RenderManager(windowManager.Window);
        world = new World(renderManager.gl, windowManager.Window, inputManager.InputContext);

        inputManager.Mouse?.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Right) placeTimer = MAX_PLACE_TIMER;
        };

        inputManager.Mouse?.Scroll += (_, wheel) => world.Inventory.Scroll(wheel.Y);

        var script = Environment.GetEnvironmentVariable(VisualScriptVariable);
        if (!string.IsNullOrWhiteSpace(script))
        {
            try
            {
                visualDirector = new VisualDirector(VisualScenario.Load(script.Trim()), world);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Ignoring {VisualScriptVariable}=\"{script}\": {e.Message}");
                visualScriptFailed = true;
            }
        }
    }

    /// <summary>
    /// One line naming the build the timings were taken on. A Debug build, an attached
    /// debugger and workstation GC each cost enough that a measurement without them recorded
    /// cannot be compared against anything.
    /// </summary>
    private static void PrintBuildDiagnostics()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        Console.WriteLine(
            $"Build: {configuration} | debugger {Debugger.IsAttached} | serverGC {GCSettings.IsServerGC}"
            + $" | latency {GCSettings.LatencyMode} | cpus {Environment.ProcessorCount}");
    }

    /// <summary>
    /// Dev override: VOXEL_TIME="seconds" offsets the clock the lighting pass reads, so noon,
    /// dusk or night can be photographed straight away instead of after minutes of orbit.
    /// Parsed once here; an unparseable value is ignored with a warning, like VOXEL_SPAWN.
    /// </summary>
    private static double ReadTimeOffset()
    {
        var timeOverride = Environment.GetEnvironmentVariable(TimeOffsetVariable);
        if (string.IsNullOrWhiteSpace(timeOverride))
            return 0.0;

        if (double.TryParse(timeOverride.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var offset))
            return offset;

        Console.WriteLine(
            $"Ignoring {TimeOffsetVariable}=\"{timeOverride}\": expected a number of seconds, e.g. 150");
        return 0.0;
    }


    private static void OnUpdate(double deltaTime)
    {
        if (visualScriptFailed)
        {
            windowManager.Window.Close();
            return;
        }

        if (visualDirector != null)
        {
            visualDirector.Drive(deltaTime);
            if (visualDirector.IsDone)
                windowManager.Window.Close();
            return;
        }

        placeTimer = Math.Min(MAX_PLACE_TIMER, placeTimer + deltaTime);
        var xPressed = inputManager.Keyboard?.IsKeyPressed(Key.X) == true;
        if (xPressed && !xWasPressed)
        {
            debugEnabled = !debugEnabled;
            Console.WriteLine($"Debug {(debugEnabled ? "ON" : "OFF")}");
        }
        xWasPressed = xPressed;

        // One throw per press, so holding Q doesn't empty the slot; Ctrl+Q throws the stack
        var qPressed = inputManager.Keyboard?.IsKeyPressed(Key.Q) == true;
        if (qPressed && !qWasPressed)
        {
            var wholeStack = inputManager.Keyboard?.IsKeyPressed(Key.ControlLeft) == true
                || inputManager.Keyboard?.IsKeyPressed(Key.ControlRight) == true;
            world.ThrowSelected(wholeStack);
        }
        qWasPressed = qPressed;

        if (inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true && placeTimer >= MAX_PLACE_TIMER)
        {
            placeTimer = 0;
            world.TryUseSelected(World.InteractionReach);
        }

        world.Update((float)deltaTime, inputManager.Keyboard);

        FrameProfiler.Begin(ProfileSection.Breaking);
        world.UpdateBlockBreaking(deltaTime, inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);
        FrameProfiler.End(ProfileSection.Breaking);

        if (debugEnabled)
        {
            // One profiler snapshot a second, so a headless run's log carries the breakdown
            if (FrameProfiler.TryTakeSnapshotForConsole(out var profilerLine))
                Console.WriteLine(profilerLine);

            debugLogTimer += deltaTime;
            if (debugLogTimer >= 0.2)
            {
                var fps = deltaTime > 0 ? (int)(1 / deltaTime) : 0;
                var camera = world.Camera;
                var position = camera.Position;
                Console.WriteLine(
                    $"FPS {fps} | Pos ({position.X:F2}, {position.Y:F2}, {position.Z:F2}) | Grounded {camera.IsGrounded} | Vy {camera.VerticalVelocity:F2} | Water {camera.IsInWater}/{camera.IsEyeInWater}");
                debugLogTimer = 0;
            }
        }

        if (inputManager.Keyboard?.IsKeyPressed(Key.Escape) == true)
            windowManager.Window.Close();
    }

    private static void OnRender(double deltaTime)
    {
        // One TIME_ELAPSED query wraps the whole frame, so it has to open before any draw
        renderManager.BeginGpuFrame();

        FrameProfiler.Begin(ProfileSection.Lighting);
        renderManager.SetLighting(windowManager.Window.Time + timeOffset, world.Camera, world.PrimedTnt,
            world.Camera.IsEyeInWater);
        renderManager.BeginFrame(world.Camera);
        FrameProfiler.End(ProfileSection.Lighting);

        world.Render(renderManager);
        fpsTimer += deltaTime;
        fpsFrames++;
        if (fpsTimer >= 1.0)
        {
            shownFps = (int)(fpsFrames / fpsTimer);
            fpsTimer = 0.0;
            fpsFrames = 0;
        }

        FrameProfiler.Begin(ProfileSection.UiPass);
        var hideHud = visualDirector?.TakeScreenshot == true && visualDirector.ScreenshotHideHud;
        if (!hideHud)
            world.RenderUI(renderManager, shownFps, debugEnabled);
        FrameProfiler.End(ProfileSection.UiPass);

        if (visualDirector?.TakeScreenshot == true)
        {
            visualDirector.TakeScreenshot = false;
            var size = windowManager.Window.FramebufferSize;
            VisualCapture.SaveScreenshot(renderManager.gl, size.X, size.Y, visualDirector.ScreenshotPath);
        }

        renderManager.EndGpuFrame();
        FrameProfiler.EndFrame();
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        renderManager.SetViewport(size);
    }

    private static void OnClosing()
    {
        renderManager?.Dispose();
        inputManager?.Dispose();
    }
}