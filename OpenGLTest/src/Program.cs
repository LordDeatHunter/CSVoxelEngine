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
    private static AmbienceManager? ambience;
    private static World world;
    private static VisualDirector? visualDirector;
    private static bool visualScriptFailed;
    private static bool debugEnabled;
    private static bool xWasPressed;
    private static bool qWasPressed;
    private static bool eWasPressed;
    private static bool escWasPressed;
    private static readonly InventoryView inventoryView = new();
    private static readonly PauseMenuView pauseMenuView = new();
    private static readonly SettingsMenuView settingsMenuView = new();
    private static bool appliedVSync;
    private static int appliedResolution = -1;
    private static int appliedMaxFps = -1;
    private static double debugLogTimer;
    private static double fpsTimer;
    private static int fpsFrames;
    private static int shownFps;
    // TODO: move this to player class
    private static double placeTimer = MAX_PLACE_TIMER;
    private static readonly double MAX_PLACE_TIMER = 0.2;
    // Hotbar hotkeys 1-0, hoisted so the per-frame poll allocates nothing.
    private static readonly Key[] NumberKeys =
        [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
            Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0];

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
        // PERF_TODO item 10: keep full blocking Gen2s off the frame path. SustainedLowLatency
        // defers them in favour of background collections; the startup line already logs the
        // effective latency mode via PrintBuildDiagnostics, so a perf capture records it.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        GameSettings.ResetToDefaults();
        GameSettings.Load();
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
        world.Player.Rig = renderManager.PlayerRig;

        inputManager.Mouse?.MouseUp += (_, button) =>
        {
            if (pauseMenuView.IsOpen)
            {
                if (button == MouseButton.Left && inputManager.Mouse != null)
                {
                    if (pauseMenuView.Page == PauseMenuPage.Settings)
                        HandleSettingsClick(inputManager.Mouse.Position);
                    else
                        HandleMenuClick(inputManager.Mouse.Position);
                }
                if (settingsMenuView.DragRow.HasValue)
                    GameSettings.Save();
                settingsMenuView.DragRow = null;
                return;
            }
            if (button == MouseButton.Right) placeTimer = MAX_PLACE_TIMER;
            if (button == MouseButton.Left && inventoryView.IsOpen && inputManager.Mouse != null)
                HandleInventoryClick(inputManager.Mouse.Position);
        };

        inputManager.Mouse?.MouseDown += (_, button) =>
        {
            if (!pauseMenuView.IsOpen || pauseMenuView.Page != PauseMenuPage.Settings
                || button != MouseButton.Left || inputManager.Mouse == null)
                return;
            var hit = renderManager.UiSettingsHitAt(inputManager.Mouse.Position, settingsMenuView);
            if (hit.Kind == SettingsHitKind.Slider && SettingsRows.All[hit.Row] is SliderRow slider)
            {
                settingsMenuView.DragRow = hit.Row;
                slider.Set(SettingsLayout.SliderValue(slider, hit.Fraction));
            }
        };

        inputManager.Mouse?.Scroll += (_, wheel) =>
        {
            if (!inventoryView.IsOpen && !pauseMenuView.IsOpen) world.Inventory.Scroll(wheel.Y);
        };

        ambience = new AmbienceManager();

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
                Log.WriteLine($"Ignoring {VisualScriptVariable}=\"{script}\": {e.Message}");
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
        Log.WriteLine(
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

        Log.WriteLine(
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
            Log.WriteLine(profilerLine);

        debugLogTimer += deltaTime;
        if (debugLogTimer < 0.2) return;

        var fps = deltaTime > 0 ? (int)(1 / deltaTime) : 0;
        var camera = world.Camera;
        var position = camera.Position;
        Log.WriteLine(
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
            FrameProfiler.Begin(ProfileSection.ProgramUpdate);
            visualDirector.Drive(deltaTime);
            LogDebugLine(deltaTime);
            if (visualDirector.IsDone)
                windowManager.Window.Close();
            FrameProfiler.End(ProfileSection.ProgramUpdate);
            return;
        }

        FrameProfiler.Begin(ProfileSection.ProgramUpdate);

        placeTimer = Math.Min(MAX_PLACE_TIMER, placeTimer + deltaTime);
        var xPressed = inputManager.Keyboard?.IsKeyPressed(Key.X) == true;
        if (xPressed && !xWasPressed)
        {
            debugEnabled = !debugEnabled;
            Log.WriteLine($"Debug {(debugEnabled ? "ON" : "OFF")}");
        }
        xWasPressed = xPressed;

        // ESC toggles the pause menu instead of closing the game: first press opens it,
        // ESC on the settings page steps back to the main page, ESC there resumes the game.
        // An open [E] panel is closed first so the two never overlap.
        var escPressed = inputManager.Keyboard?.IsKeyPressed(Key.Escape) == true;
        if (escPressed && !escWasPressed)
        {
            if (inventoryView.IsOpen)
                SetInventoryOpen(false);
            else if (pauseMenuView.Page == PauseMenuPage.Closed)
                SetPauseMenu(PauseMenuPage.Main);
            else if (pauseMenuView.Page == PauseMenuPage.Settings)
                SetPauseMenu(PauseMenuPage.Main);
            else
                SetPauseMenu(PauseMenuPage.Closed);
        }
        escWasPressed = escPressed;

        SyncWindowSettings();

        // A slider drag tracks the cursor every frame until release (which saves).
        if (pauseMenuView.Page == PauseMenuPage.Settings && settingsMenuView.DragRow.HasValue
            && SettingsRows.All[settingsMenuView.DragRow.Value] is SliderRow drag
            && inputManager.Mouse != null)
        {
            var tabRow = SettingsLayout.TabRowOf(settingsMenuView.Tab, settingsMenuView.DragRow.Value);
            if (tabRow < 0 || !inputManager.Mouse.IsButtonPressed(MouseButton.Left))
            {
                settingsMenuView.DragRow = null;
                GameSettings.Save();
            }
            else
            {
                var screen = new System.Numerics.Vector2(
                    windowManager.Window.Size.X, windowManager.Window.Size.Y);
                drag.Set(SettingsLayout.SliderValue(drag,
                    SettingsLayout.SliderFraction(inputManager.Mouse.Position.X, tabRow, screen)));
            }
        }

        // The [E] panel owns the mouse: clicks equip, hotbar keys rest, and the world runs on
        var ePressed = inputManager.Keyboard?.IsKeyPressed(Key.E) == true;
        if (ePressed && !eWasPressed && !pauseMenuView.IsOpen)
            SetInventoryOpen(!inventoryView.IsOpen);
        eWasPressed = ePressed;

        if (!inventoryView.IsOpen && !pauseMenuView.IsOpen)
        {
            // 1-10 grabs the hotbar slot directly; scroll still walks it one step at a time
            for (var i = 0; i < NumberKeys.Length; i++)
                if (inputManager.Keyboard?.IsKeyPressed(NumberKeys[i]) == true)
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
        }
        FrameProfiler.End(ProfileSection.ProgramUpdate);

        // Right-click use/place does real work (raycast plus a synchronous remesh on bucket
        // pours and single-block placements), so it gets its own span instead of hiding in
        // the input shell.
        if (!inventoryView.IsOpen && !pauseMenuView.IsOpen
            && inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true && placeTimer >= MAX_PLACE_TIMER)
        {
            FrameProfiler.Begin(ProfileSection.UsePlace);
            placeTimer = 0;
            world.TryUseSelected(World.InteractionReach);
            FrameProfiler.End(ProfileSection.UsePlace);
        }

        // The menu freezes the simulation: the world keeps rendering behind the overlay
        // but physics, chunks, fluids and mining all hold still until resume.
        // The update below times itself; the shell spans above and below stay outside it.
        if (!pauseMenuView.IsOpen)
        {
            world.Update((float)deltaTime, inputManager.Keyboard);

            FrameProfiler.Begin(ProfileSection.Breaking);
            world.UpdateBlockBreaking(deltaTime,
                !inventoryView.IsOpen && inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);
            FrameProfiler.End(ProfileSection.Breaking);
        }

        FrameProfiler.Begin(ProfileSection.DebugLog);
        LogDebugLine(deltaTime);
        FrameProfiler.End(ProfileSection.DebugLog);
    }

    /// <summary>
    /// Opens or closes the [E] panel: the mouse is freed for clicking (and the look frozen)
    /// while it is open, then captured again. Closing also drops any list pick.
    /// </summary>
    private static void SetInventoryOpen(bool open)
    {
        inventoryView.IsOpen = open;
        if (!open)
        {
            inventoryView.ClearSelection();
            inventoryView.Craft.Clear();
        }
        world.Camera.SuppressLook = open;
        if (inputManager.Mouse != null)
            inputManager.Mouse.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    private static void SetPauseMenu(PauseMenuPage page)
    {
        if (page != PauseMenuPage.Closed && inventoryView.IsOpen)
            SetInventoryOpen(false);
        pauseMenuView.Page = page;
        if (page == PauseMenuPage.Settings)
        {
            settingsMenuView.DragRow = null;
        }
        var open = page != PauseMenuPage.Closed;
        world.Camera.SuppressLook = open;
        if (inputManager.Mouse != null)
            inputManager.Mouse.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    /// <summary>
    /// Applies the window-level settings when they changed (VSync, size, frame cap); everything
    /// else is read live by the engine. Cached so the window is only touched on change.
    /// </summary>
    private static void SyncWindowSettings()
    {
        if (appliedResolution < 0)
        {
            appliedVSync = windowManager.Window.VSync;
            appliedResolution = GameSettings.ResolutionIndex;
            appliedMaxFps = GameSettings.MaxFpsIndex;
        }
        if (GameSettings.VSync != appliedVSync)
        {
            appliedVSync = GameSettings.VSync;
            windowManager.Window.VSync = appliedVSync;
        }
        if (GameSettings.ResolutionIndex != appliedResolution)
        {
            appliedResolution = GameSettings.ResolutionIndex;
            var (w, h) = GameSettings.Resolutions[
                Math.Clamp(appliedResolution, 0, GameSettings.Resolutions.Length - 1)];
            windowManager.Window.Size = new Vector2D<int>(w, h);
        }
        if (GameSettings.MaxFpsIndex != appliedMaxFps)
        {
            appliedMaxFps = GameSettings.MaxFpsIndex;
            var fps = GameSettings.MaxFpsOptions[
                Math.Clamp(appliedMaxFps, 0, GameSettings.MaxFpsOptions.Length - 1)];
            windowManager.Window.UpdatesPerSecond = fps;
            windowManager.Window.FramesPerSecond = fps;
        }
    }

    private static void HandleSettingsClick(System.Numerics.Vector2 position)
    {
        var hit = renderManager.UiSettingsHitAt(position, settingsMenuView);
        switch (hit.Kind)
        {
            case SettingsHitKind.Back:
                SetPauseMenu(PauseMenuPage.Main);
                break;
            case SettingsHitKind.Tab:
                settingsMenuView.Tab = SettingsRows.Tabs[Math.Clamp(hit.Row, 0, SettingsRows.Tabs.Length - 1)];
                settingsMenuView.DragRow = null;
                break;
            case SettingsHitKind.Toggle when SettingsRows.All[hit.Row] is ToggleRow toggle:
                toggle.Set(!toggle.Get());
                GameSettings.Save();
                break;
            case SettingsHitKind.Slider when SettingsRows.All[hit.Row] is SliderRow slider:
                slider.Set(SettingsLayout.SliderValue(slider, hit.Fraction));
                GameSettings.Save();
                break;
            case SettingsHitKind.Options when SettingsRows.All[hit.Row] is OptionsRow options:
                options.Set((options.Get() + 1) % options.Options.Length);
                GameSettings.Save();
                break;
        }
    }

    private static void HandleMenuClick(System.Numerics.Vector2 position)
    {
        switch (renderManager.UiPauseMenuHitAt(position, pauseMenuView))
        {
            case PauseMenuHit.Resume:
                SetPauseMenu(PauseMenuPage.Closed);
                break;
            case PauseMenuHit.Settings:
                SetPauseMenu(PauseMenuPage.Settings);
                break;
            case PauseMenuHit.Exit:
                windowManager.Window.Close();
                break;
            case PauseMenuHit.Back:
                SetPauseMenu(PauseMenuPage.Main);
                break;
        }
    }

    /// <summary>
    /// Routes one left-click inside the open panel: tabs switch, rows pick for preview,
    /// crafting controls feed the open recipe, and hotbar slots equip the pick (clicking
    /// what the slot already shows clears it).
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
            InventoryCategory.Crafting => ContentRegistry.CraftingRecipes.Count,
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
                inventoryView.Craft.Select(ContentRegistry.CraftingRecipes[row.Value]);
            }
            else
            {
                var picked = inventory.StackEntries(inventoryView.Tab)[row.Value].Id;
                inventoryView.SelectedId = picked;
                inventoryView.SelectedToolInstance = 0;
                if (inventoryView.Craft.Recipe != null)
                    inventoryView.Craft.FeedFromInventory(picked, inventory);
            }
            return;
        }

        if (inventoryView.Tab == InventoryCategory.Crafting)
        {
            var hit = renderManager.UiCraftHitAt(position, inventoryView);
            var craft = inventoryView.Craft;
            switch (hit.Kind)
            {
                case CraftHitKind.BatchMinus:
                    craft.SetBatch(craft.Batch - 1);
                    return;
                case CraftHitKind.BatchPlus:
                    craft.SetBatch(craft.Batch + 1);
                    return;
                case CraftHitKind.IngMinus:
                    craft.FeedLess(hit.IngredientIndex);
                    return;
                case CraftHitKind.IngPlus:
                    craft.FeedMore(hit.IngredientIndex, inventory);
                    return;
                case CraftHitKind.MemberChip:
                    craft.SetMember(hit.IngredientIndex, hit.MemberId, inventory);
                    craft.FeedMore(hit.IngredientIndex, inventory);
                    return;
                case CraftHitKind.CraftButton:
                    craft.Craft(inventory);
                    return;
                case CraftHitKind.None:
                    break;
            }
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
        FrameProfiler.Begin(ProfileSection.RenderShell);
        renderManager.BeginGpuFrame();
        FrameProfiler.End(ProfileSection.RenderShell);

        FrameProfiler.Begin(ProfileSection.Lighting);
        renderManager.BeginGpuPass(ProfileSection.Lighting);
        renderManager.SetLighting(windowManager.Window.Time + timeOffset, world.Camera, world.PrimedTnt,
            world.Camera.IsEyeInWater, world.Camera.IsEyeInLava, world.Campfires.LitCells);
        ambience?.Update(renderManager.Dayness, (float)Math.Clamp(deltaTime, 0.0, 0.25));
        renderManager.BeginFrame(world.Camera);
        renderManager.EndGpuPass(ProfileSection.Lighting);
        FrameProfiler.End(ProfileSection.Lighting);

        world.Render(renderManager);

        FrameProfiler.Begin(ProfileSection.RenderShell);
        fpsTimer += deltaTime;
        fpsFrames++;
        if (fpsTimer >= 1.0)
        {
            shownFps = (int)(fpsFrames / fpsTimer);
            fpsTimer = 0.0;
            fpsFrames = 0;
        }
        FrameProfiler.End(ProfileSection.RenderShell);

        FrameProfiler.Begin(ProfileSection.UiPass);
        renderManager.BeginGpuPass(ProfileSection.UiPass);
        var hideHud = visualDirector?.TakeScreenshot == true && visualDirector.ScreenshotHideHud;
        if (!hideHud)
            world.RenderUI(renderManager, inventoryView,
                (float)(windowManager.Window.Time + timeOffset), shownFps, debugEnabled,
                showCoordinates: true, menu: pauseMenuView, settings: settingsMenuView);
        renderManager.EndGpuPass(ProfileSection.UiPass);
        FrameProfiler.End(ProfileSection.UiPass);

        // Timed even when no screenshot is due, so the pairing stays obvious; an empty span
        // costs one Stopwatch read.
        FrameProfiler.Begin(ProfileSection.Screenshot);
        if (visualDirector?.TakeScreenshot == true)
        {
            visualDirector.TakeScreenshot = false;
            var size = windowManager.Window.FramebufferSize;
            var path = visualDirector.ScreenshotPath;
            var pixels = VisualCapture.ReadPixels(renderManager.gl, size.X, size.Y);
            VisualCapture.QueueSave(pixels, size.X, size.Y, path);
        }
        FrameProfiler.End(ProfileSection.Screenshot);

        FrameProfiler.Begin(ProfileSection.RenderShell);
        renderManager.EndGpuFrame();
        FrameProfiler.End(ProfileSection.RenderShell);
        FrameProfiler.EndFrame();
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        renderManager.SetViewport(size);
    }

    private static void OnClosing()
    {
        ambience?.Dispose();
        // The last scripted step is usually a screenshot whose encode is still on a
        // worker; drain it before tearing down GL and the log queue.
        VisualCapture.WaitForPending();
        renderManager?.Dispose();
        inputManager?.Dispose();
        Log.Shutdown();
    }
}