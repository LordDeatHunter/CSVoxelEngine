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
    private static bool eWasPressed;
    private static readonly InventoryView inventoryView = new();
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

    // Set to 1 to start with the X debug line on, so a scripted run logs FPS and the profiler
    // breakdown once a second without anyone pressing a key
    private const string DebugVariable = "VOXEL_DEBUG";

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
        world = new World(renderManager.gl, renderManager.OpaqueChunkGeometry,
            renderManager.TranslucentChunkGeometry, windowManager.Window, inputManager.InputContext);

        inputManager.Mouse?.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Right) placeTimer = MAX_PLACE_TIMER;
            if (button == MouseButton.Left && inventoryView.IsOpen && inputManager.Mouse != null)
                HandleInventoryClick(inputManager.Mouse.Position);
        };

        inputManager.Mouse?.Scroll += (_, wheel) =>
        {
            if (!inventoryView.IsOpen) world.Inventory.Scroll(wheel.Y);
        };

        debugEnabled = Environment.GetEnvironmentVariable(DebugVariable)?.Trim() is "1" or "true";

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


    /// <summary>
    /// The X debug line: FPS and camera state five times a second plus one profiler snapshot a
    /// second. Shared by the interactive and the scripted update paths, so a VOXEL_DEBUG run logs
    /// the same numbers a player would see.
    /// </summary>
    private static void LogDebugLine(double deltaTime)
    {
        if (!debugEnabled) return;

        if (FrameProfiler.TryTakeSnapshotForConsole(out var profilerLine))
            Console.WriteLine(profilerLine);

        debugLogTimer += deltaTime;
        if (debugLogTimer < 0.2) return;

        var fps = deltaTime > 0 ? (int)(1 / deltaTime) : 0;
        var camera = world.Camera;
        var position = camera.Position;
        Console.WriteLine(
            $"FPS {fps} | Pos ({position.X:F2}, {position.Y:F2}, {position.Z:F2}) | Grounded {camera.IsGrounded} | Vy {camera.VerticalVelocity:F2} | Water {camera.IsInWater}/{camera.IsEyeInWater}");
        debugLogTimer = 0;
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
            LogDebugLine(deltaTime);
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

        // The [E] panel owns the mouse: clicks equip, hotbar keys rest, and the world runs on
        var ePressed = inputManager.Keyboard?.IsKeyPressed(Key.E) == true;
        if (ePressed && !eWasPressed)
            SetInventoryOpen(!inventoryView.IsOpen);
        eWasPressed = ePressed;

        if (!inventoryView.IsOpen)
        {
            // 1-10 grabs the hotbar slot directly; scroll still walks it one step at a time
            var numberKeys = new[] { Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
                Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0 };
            for (var i = 0; i < numberKeys.Length; i++)
                if (inputManager.Keyboard?.IsKeyPressed(numberKeys[i]) == true)
                    world.Inventory.Select(i);

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
        }

        world.Update((float)deltaTime, inputManager.Keyboard);

        FrameProfiler.Begin(ProfileSection.Breaking);
        world.UpdateBlockBreaking(deltaTime,
            !inventoryView.IsOpen && inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);
        FrameProfiler.End(ProfileSection.Breaking);

        LogDebugLine(deltaTime);

        if (inputManager.Keyboard?.IsKeyPressed(Key.Escape) == true)
        {
            if (inventoryView.IsOpen)
                SetInventoryOpen(false);
            else
                windowManager.Window.Close();
        }
    }

    /// <summary>
    /// Opens or closes the [E] panel: the mouse is freed for clicking (and the look frozen)
    /// while it is open, then captured again. Closing also drops any list pick.
    /// </summary>
    private static void SetInventoryOpen(bool open)
    {
        inventoryView.IsOpen = open;
        if (!open)
            inventoryView.ClearSelection();
        world.Camera.SuppressLook = open;
        if (inputManager.Mouse != null)
            inputManager.Mouse.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    /// <summary>
    /// Routes one left-click inside the open panel: tabs switch, rows pick for preview,
    /// and hotbar slots equip the pick (clicking what the slot already shows clears it).
    /// </summary>
    private static void HandleInventoryClick(System.Numerics.Vector2 position)
    {
        var inventory = world.Inventory;

        var tab = renderManager.UiPanelTabAt(position);
        if (tab.HasValue)
        {
            inventoryView.Tab = tab.Value;
            inventoryView.ClearSelection();
            return;
        }

        var rowCount = inventoryView.Tab switch
        {
            InventoryCategory.Tools => inventory.ToolEntries().Count,
            InventoryCategory.Crafting => inventory.Craftable().Count,
            _ => inventory.StackEntries(inventoryView.Tab).Count,
        };
        var row = renderManager.UiPanelRowAt(position, rowCount);
        if (row.HasValue)
        {
            if (inventoryView.Tab == InventoryCategory.Tools)
            {
                inventoryView.SelectedToolInstance = inventory.ToolEntries()[row.Value].InstanceId;
                inventoryView.SelectedId = 0;
            }
            else if (inventoryView.Tab == InventoryCategory.Crafting)
            {
                var recipe = inventory.Craftable()[row.Value];
                if (inventoryView.SelectedId == recipe.ResultId)
                    inventory.TryCraft(recipe);
                else
                {
                    inventoryView.SelectedId = recipe.ResultId;
                    inventoryView.SelectedToolInstance = 0;
                }
            }
            else
            {
                inventoryView.SelectedId = inventory.StackEntries(inventoryView.Tab)[row.Value].Id;
                inventoryView.SelectedToolInstance = 0;
            }
            return;
        }

        var hotbar = renderManager.UiHotbarSlotAt(position);
        if (hotbar.HasValue)
        {
            if (inventoryView.Tab == InventoryCategory.Tools && inventoryView.SelectedToolInstance != 0)
                inventory.EquipTool(hotbar.Value, inventoryView.SelectedToolInstance);
            else if (inventoryView.SelectedId != 0)
                inventory.EquipStackable(hotbar.Value, inventoryView.SelectedId);
        }
    }

    private static void OnRender(double deltaTime)
    {
        // One TIME_ELAPSED query wraps the whole frame, so it has to open before any draw
        renderManager.BeginGpuFrame();

        FrameProfiler.Begin(ProfileSection.Lighting);
        renderManager.SetLighting(windowManager.Window.Time + timeOffset, world.Camera, world.PrimedTnt,
            world.Camera.IsEyeInWater, world.Camera.IsEyeInLava);
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
            world.RenderUI(renderManager, inventoryView,
                (float)(windowManager.Window.Time + timeOffset), shownFps, debugEnabled);
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