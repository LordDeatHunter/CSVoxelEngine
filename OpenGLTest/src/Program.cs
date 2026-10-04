using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace silkgltriangle;

/// <summary>
/// Entry point and window wiring. The game boots to the main menu (<see cref="MainMenuController"/>)
/// unless a dev override is set (<see cref="LaunchMode.BypassesMenu"/>), in which case it builds a
/// storeless <see cref="World"/> straight away exactly as before. PLAY/CREATE and the pause menu's
/// third button, SAVE &amp; QUIT, are deferred one frame: the click only records the work, the next
/// render presents the loading or saving screen, and the update after that runs the blocking call.
/// </summary>
public static class Program
{
    /// <summary>What the window is showing: a menu, a world streaming in, a world, or a teardown.</summary>
    private enum AppScreen { Menu, Loading, Playing, Saving }

    /// <summary>Blocking work recorded by a click and run on the update after its screen was presented.</summary>
    private enum PendingWork { None, OpenWorld, SaveAndQuit }

    private static WindowManager windowManager;
    private static InputManager inputManager;
    private static RenderManager renderManager;
    private static AmbienceManager? ambience;
    private static SfxManager? sfx;
    private static AppScreen screen;
    private static World? world;
    private static WorldStore worldStore = null!;
    private static ServerStore serverStore = null!;
    private static MainMenuController menu = null!;
    private static readonly LoadingScreenView loadingView = new();
    // The saved world being played; null on the menu and for a storeless dev-override world
    private static WorldInfo? currentWorld;
    private static PendingWork pending;
    private static WorldInfo? pendingWorld;
    private static bool pendingWorldIsNew;
    // Set only by OnRender once the announcing frame of the pending work was drawn, so that
    // frame can never be skipped; the blocking call then runs on the next update.
    private static bool pendingFramePresented;
    private static VisualDirector? visualDirector;
    private static bool visualScriptFailed;
    private static bool debugEnabled;
    private static bool xWasPressed;
    private static bool qWasPressed;
    private static bool rightWasDown;
    private static bool middleWasPressed;
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

    // LaunchMode.ScriptVariable (VOXEL_SCRIPT) names a scenario JSON to run a scripted visual test
    // instead of playing: teleport/walk/mine/screenshot steps drive the real update functions, then
    // the window closes. A bad path or bad script fails fast instead of trapping anyone.

    // Set to 1 to start with the X debug line on, so a scripted run logs FPS and the profiler
    // breakdown once a second without anyone pressing a key
    private const string DebugVariable = "VOXEL_DEBUG";

    // VOXEL_TIME: seeds World.TimeOffset once after construction, so a screenshot can be taken at
    // any point of the day without waiting for the sun to get there. The World owns the lighting
    // clock from then on (World.LightingTime), and a restored world overrides this offset. A world
    // opened from the menu is rebased by the window clock at construction, so it starts at the
    // same time of day however long the menu was open.
    private static double timeOffset;

    /// <summary>
    /// The one test for "a settings page is up", shared by the drag start (MouseDown), the
    /// per-frame drag tracking (OnUpdate) and the release/save (MouseUp), so a slider drags and
    /// saves identically on the main menu and the pause menu.
    /// </summary>
    private static bool SettingsPageOpen =>
        (screen == AppScreen.Menu && menu.View.Page == MainMenuPage.Settings)
        || (screen == AppScreen.Playing && pauseMenuView.Page == PauseMenuPage.Settings);

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
        sfx = new SfxManager();
        worldStore = new WorldStore(WorldStore.DefaultRoot);
        serverStore = new ServerStore(ServerStore.DefaultPath);
        menu = new MainMenuController(worldStore, serverStore, Random.Shared.Next);

        // A dev override (VOXEL_SCRIPT/SPAWN/LOOK) takes the old path unchanged: a storeless world
        // built right here, no seed change, no persistence and no menu, so scripted runs stay
        // byte-identical and an override can never reach a saved world.
        var bypassMenu = LaunchMode.BypassesMenu(Environment.GetEnvironmentVariable);
        if (bypassMenu)
        {
            var devWorld = new World(renderManager.gl, renderManager.OpaqueChunkGeometry,
                renderManager.TranslucentChunkGeometry, renderManager.LodGeometry,
                windowManager.Window, inputManager.InputContext, sfx);
            world = devWorld;
            devWorld.TimeOffset = timeOffset;
            devWorld.Player.Rig = renderManager.PlayerRig;
            // Entity draws sample their light from the live chunks from here on
            renderManager.LightSource = devWorld.GetChunkManager();
            screen = AppScreen.Playing;
        }

        inputManager.Mouse?.MouseUp += (_, button) =>
        {
            if (screen == AppScreen.Menu)
            {
                if (button == MouseButton.Left && inputManager.Mouse != null)
                {
                    var pos = inputManager.Mouse.Position;
                    if (menu.View.Page == MainMenuPage.Settings)
                        HandleSettingsClick(pos);
                    else
                        HandleMenuAction(menu.Click(renderManager.UiMainMenuHitAt(pos, menu.View)));
                }
                if (settingsMenuView.DragRow.HasValue)
                    GameSettings.Save();
                settingsMenuView.DragRow = null;
                return;
            }
            if (screen != AppScreen.Playing) return;

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
            {
                // Releasing a thumb drag ends it here and nowhere else while the panel is
                // open, so the release never equips or picks whatever is under the cursor.
                if (inventoryView.ThumbDragGrab.HasValue)
                {
                    inventoryView.EndThumbDrag();
                    return;
                }
                HandleInventoryClick(inputManager.Mouse.Position);
            }
        };

        inputManager.Mouse?.MouseDown += (_, button) =>
        {
            if (screen is AppScreen.Loading or AppScreen.Saving) return;
            if (screen == AppScreen.Playing && inventoryView.IsOpen && !pauseMenuView.IsOpen
                && button == MouseButton.Left && inputManager.Mouse != null)
            {
                HandleInventoryScrollbarPress(inputManager.Mouse.Position);
                return;
            }
            if (!SettingsPageOpen || button != MouseButton.Left || inputManager.Mouse == null)
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
            if (screen == AppScreen.Menu)
            {
                // wheel.Y > 0 is toward the top, i.e. a smaller offset
                if (inputManager.Mouse != null && menu.View.Page == MainMenuPage.Worlds
                    && renderManager.UiIsOverWorldList(inputManager.Mouse.Position))
                    menu.Scroll(-wheel.Y * MainMenuLayout.WheelStepRows, renderManager.UiCanvasSize);
                else if (inputManager.Mouse != null && menu.View.Page == MainMenuPage.Multiplayer
                    && renderManager.UiIsOverServerList(inputManager.Mouse.Position))
                    menu.Scroll(-wheel.Y * MainMenuLayout.WheelStepRows, renderManager.UiCanvasSize);
                return;
            }
            if (screen != AppScreen.Playing) return;
            var w = world!;

            if (!inventoryView.IsOpen && !pauseMenuView.IsOpen) w.Inventory.Scroll(wheel.Y);
            else if (inventoryView.IsOpen && !pauseMenuView.IsOpen && inputManager.Mouse != null)
            {
                // The wheel scrolls the list only while hovering it; wheel.Y > 0 is toward the top.
                var canvas = renderManager.UiCanvasSize;
                var p = renderManager.UiToCanvas(inputManager.Mouse.Position);
                if (InventoryPanelLayout.IsOverList(p, canvas))
                {
                    var searching = !inventoryView.Search.IsEmpty;
                    var rowCount = searching
                        ? InventorySearch.Search(inventoryView.Search.Query, w.Inventory, w.Camera.IsCreative).Count
                        : RowCountFor(inventoryView.Tab);
                    var delta = -wheel.Y * InventoryPanelLayout.WheelStepRows * InventoryPanelLayout.RowHeight;
                    SetListScroll(InventoryPanelLayout.ClampedOffset(
                        (searching ? inventoryView.Search.ScrollOffset : inventoryView.ScrollOffset) + delta,
                        rowCount, canvas), searching);
                }
                else if (inventoryView.Tab == InventoryCategory.Crafting
                         && renderManager.UiIsOverCraftCard(inputManager.Mouse.Position))
                {
                    inventoryView.CraftScrollOffset = renderManager.UiClampCraftScroll(
                        inventoryView.CraftScrollOffset - wheel.Y * CraftCardLayout.WheelStep);
                }
            }
        };

        // Menu text input: characters through KeyChar, the four control keys through KeyDown. Both
        // act only while the main menu is showing, and ESC for the menu lives here alone (the
        // polled ESC below is world-only), so one press never makes two transitions. While a world
        // plays, the same events feed the [E] panel's search field instead.
        if (inputManager.Keyboard is { } keyboard)
        {
            keyboard.KeyChar += (_, c) =>
            {
                if (screen == AppScreen.Menu)
                    menu.Type(c);
                else if (screen == AppScreen.Playing && inventoryView.IsOpen && !pauseMenuView.IsOpen)
                    HandleInventoryType(c);
            };
            keyboard.KeyDown += (_, key, _) =>
            {
                if (screen == AppScreen.Playing)
                {
                    if (inventoryView.IsOpen && !pauseMenuView.IsOpen)
                        HandleInventoryKey(key);
                    return;
                }
                if (screen != AppScreen.Menu) return;
                var menuKey = key switch
                {
                    Key.Backspace => MenuKey.Backspace,
                    Key.Tab => MenuKey.Tab,
                    Key.Enter or Key.KeypadEnter => MenuKey.Enter,
                    Key.Escape => MenuKey.Escape,
                    _ => (MenuKey?)null,
                };
                if (menuKey == null) return;
                // ESC leaves the settings page; a live slider drag is saved like a release would
                if (menuKey == MenuKey.Escape && menu.View.Page == MainMenuPage.Settings)
                {
                    if (settingsMenuView.DragRow.HasValue)
                        GameSettings.Save();
                    settingsMenuView.DragRow = null;
                }
                HandleMenuAction(menu.Key(menuKey.Value));
            };
        }

        ambience = new AmbienceManager();

        debugEnabled = Environment.GetEnvironmentVariable(DebugVariable)?.Trim() is "1" or "true";

        if (!bypassMenu)
        {
            ShowMenu();
            return;
        }

        var script = Environment.GetEnvironmentVariable(LaunchMode.ScriptVariable);
        if (!string.IsNullOrWhiteSpace(script))
        {
            try
            {
                visualDirector = new VisualDirector(VisualScenario.Load(script.Trim()), world!);
            }
            catch (Exception e)
            {
                Log.WriteLine($"Ignoring {LaunchMode.ScriptVariable}=\"{script}\": {e.Message}");
                visualScriptFailed = true;
            }
        }
    }

    /// <summary>Shows the main menu's first page with a free cursor.</summary>
    private static void ShowMenu()
    {
        screen = AppScreen.Menu;
        menu.OpenMain();
        if (inputManager.Mouse != null)
            inputManager.Mouse.Cursor.CursorMode = CursorMode.Normal;
    }

    /// <summary>Acts on what a main-menu click or key asked for.</summary>
    private static void HandleMenuAction(MainMenuAction action)
    {
        switch (action)
        {
            case MainMenuAction.Play:
                var request = menu.TakePlayRequest();
                if (request != null)
                    RequestOpenWorld(request, menu.PlayRequestIsNew);
                break;
            case MainMenuAction.Connect:
                var target = menu.TakeConnectRequest();
                if (target is { } connect)
                    StartMultiplayer(connect);
                break;
            case MainMenuAction.Exit:
                windowManager.Window.Close();
                break;
        }
    }

    /// <summary>
    /// Opens a multiplayer session to the chosen server: the client connects, the world streams from
    /// the server and plays like a saved world. Wired to the networking layer in the next milestone.
    /// </summary>
    private static void StartMultiplayer(ConnectTarget target)
    {
        Log.WriteLine($"Multiplayer: connect {target.Address} ({target.Name})");
        menu.ShowNotice("Multiplayer is not connected yet");
    }

    /// <summary>
    /// Records a world to open and switches to the loading screen; the world itself is built on
    /// the update after that screen was presented (<see cref="PerformPending"/>), so the click
    /// frame never carries the construction spike.
    /// </summary>
    private static void RequestOpenWorld(WorldInfo info, bool isNew)
    {
        pending = PendingWork.OpenWorld;
        pendingWorld = info;
        pendingWorldIsNew = isNew;
        pendingFramePresented = false;
        loadingView.Begin(LoadingScreenKind.Loading, info.Name,
            isNew ? "Generating terrain..." : "Loading world...");
        screen = AppScreen.Loading;
    }

    /// <summary>
    /// SAVE &amp; QUIT: shows the saving screen first; the blocking
    /// <see cref="World.SaveAndDispose"/> runs on the update after it was presented.
    /// </summary>
    private static void RequestSaveAndQuit()
    {
        pending = PendingWork.SaveAndQuit;
        pendingFramePresented = false;
        loadingView.Begin(LoadingScreenKind.Saving, currentWorld?.Name ?? "", "Saving...");
        screen = AppScreen.Saving;
    }

    /// <summary>Runs the blocking work a click recorded, once its announcing frame is on screen.</summary>
    private static void PerformPending()
    {
        var work = pending;
        pending = PendingWork.None;
        switch (work)
        {
            case PendingWork.OpenWorld:
                OpenPendingWorld();
                break;
            case PendingWork.SaveAndQuit:
                SaveAndQuit();
                break;
        }
    }

    private static void OpenPendingWorld()
    {
        var info = pendingWorld;
        pendingWorld = null;
        if (info == null)
        {
            AbortOpen(null);
            return;
        }
        // A world that never finished tearing down keeps the worldgen statics in use; Apply
        // would throw, so the worlds page stays locked for this process.
        if (WorldSeed.WorldIsLive)
        {
            AbortOpen(null);
            menu.LockWorlds(SeedLockedNotice);
            return;
        }

        World opened;
        try
        {
            WorldSeed.Apply(info.Seed);
            var store = new ChunkSaveStore(info.Directory);
            opened = new World(renderManager.gl, renderManager.OpaqueChunkGeometry,
                renderManager.TranslucentChunkGeometry, renderManager.LodGeometry,
                windowManager.Window, inputManager.InputContext, sfx, store);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not open world '{info.Name}': {e.Message}");
            world = null;
            AbortOpen("Could not open the world - see the console");
            return;
        }

        world = opened;
        // Rebased so a new world starts at the default time of day (+ VOXEL_TIME) however long the
        // menu was open; a saved clock still overrides it when loading finishes.
        opened.TimeOffset = timeOffset - windowManager.Window.Time;
        // A fresh camera only learns the aspect from a resize event, which may never come
        var fb = windowManager.Window.FramebufferSize;
        if (fb.Y > 0)
            opened.Camera.SetAspectRatio(fb.X / (float)fb.Y);
        opened.Player.Rig = renderManager.PlayerRig;
        renderManager.LightSource = opened.GetChunkManager();
        currentWorld = info;
        TryTouch(info);
        ResetWorldViews();
        // The screen stays Loading until the world reports IsLoaded. The Camera constructor
        // already captured the cursor and the World holds SuppressLook until the flip.
        Log.WriteLine($"Opened world '{info.Name}' (seed {info.Seed}, {(pendingWorldIsNew ? "new" : "saved")})");
    }

    /// <summary>A failed open: back to the worlds page, with <paramref name="notice"/> under the list.</summary>
    private static void AbortOpen(string? notice)
    {
        loadingView.Finish();
        ShowMenu();
        // OpenWorlds first: it clears any notice that is not a lock notice
        menu.OpenWorlds();
        if (notice != null)
            menu.ShowNotice(notice);
    }

    private const string SeedLockedNotice = "Last world did not close cleanly - restart to play";

    private static void SaveAndQuit()
    {
        var w = world;
        var ok = true;
        if (w != null)
        {
            if (currentWorld != null)
                TryTouch(currentWorld);
            ok = w.SaveAndDispose(renderManager);
        }
        world = null;
        currentWorld = null;
        ResetWorldViews();
        loadingView.Finish();
        ShowMenu();
        menu.OpenWorlds();
        // The lock follows WorldSeed rather than the return value: a writer timeout also returns
        // false but frees the seed, and PLAY keeps working then.
        if (WorldSeed.WorldIsLive)
            menu.LockWorlds(SeedLockedNotice);
        else if (!ok)
            menu.ShowNotice("The last save may be incomplete - see the console");
    }

    /// <summary>Stamps a world as played now; a disk error only warns.</summary>
    private static void TryTouch(WorldInfo info)
    {
        try
        {
            worldStore.TouchLastPlayed(info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not update world '{info.Name}': {e.Message}");
        }
    }

    /// <summary>
    /// Drops every per-world view state and input edge, so nothing held or open in one world fires
    /// into or shows up in the next.
    /// </summary>
    private static void ResetWorldViews()
    {
        pauseMenuView.Page = PauseMenuPage.Closed;
        settingsMenuView.DragRow = null;
        inventoryView.ResetForNewWorld();
        escWasPressed = eWasPressed = qWasPressed = rightWasDown = middleWasPressed = false;
        placeTimer = MAX_PLACE_TIMER;
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

    private static void OnUpdate(double deltaTime)
    {
        if (visualScriptFailed)
        {
            windowManager.Window.Close();
            return;
        }

        if (visualDirector != null)
        {
            // Drive's world calls time themselves; wrapping it in the input shell would
            // count every world section twice (item 22), so only the shell is timed here.
            visualDirector.Drive(deltaTime);
            FrameProfiler.Begin(ProfileSection.ProgramUpdate);
            LogDebugLine(deltaTime);
            if (visualDirector.IsDone)
                windowManager.Window.Close();
            FrameProfiler.End(ProfileSection.ProgramUpdate);
            return;
        }

        // A click recorded blocking work: wait for its announcing frame, then run it. Nothing
        // else runs meanwhile, so no input reaches a world that is about to open or close.
        if (pending != PendingWork.None)
        {
            if (pendingFramePresented)
                PerformPending();
            return;
        }

        if (screen == AppScreen.Menu)
        {
            FrameProfiler.Begin(ProfileSection.ProgramUpdate);
            SyncWindowSettings();
            TrackSettingsDrag();
            FrameProfiler.End(ProfileSection.ProgramUpdate);
            return;
        }

        if (screen == AppScreen.Loading)
        {
            // The world streams on its own raised budgets; no key, mouse, pause menu or panel
            // reaches it, so Camera.SuppressLook stays the World's until its Playing flip.
            var loading = world!;
            FrameProfiler.Begin(ProfileSection.ProgramUpdate);
            SyncWindowSettings();
            FrameProfiler.End(ProfileSection.ProgramUpdate);
            loading.Update((float)deltaTime, inputManager.Keyboard);
            loadingView.Update(loading.LoadProgress, loading.IsLoaded, (float)deltaTime);
            if (loading.IsLoaded)
                screen = AppScreen.Playing;
            FrameProfiler.Begin(ProfileSection.DebugLog);
            LogDebugLine(deltaTime);
            FrameProfiler.End(ProfileSection.DebugLog);
            return;
        }

        if (screen != AppScreen.Playing) return;
        var w = world!;
        // The loading overlay fades out over the first playing frames
        if (!loadingView.IsFinished)
            loadingView.Update(1f, true, (float)deltaTime);

        FrameProfiler.Begin(ProfileSection.ProgramUpdate);

        placeTimer = Math.Min(MAX_PLACE_TIMER, placeTimer + deltaTime);
        var xPressed = inputManager.Keyboard?.IsKeyPressed(Key.X) == true;
        if (xPressed && !xWasPressed && inventoryView.Focus == InventoryFocus.None)
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
        TrackSettingsDrag();

        // A scrollbar thumb drag tracks the cursor every frame; only MouseUp (or closing the
        // panel) ends it, so there is deliberately no button-released branch here.
        if (inventoryView.IsOpen && inventoryView.ThumbDragGrab is { } grab && inputManager.Mouse != null)
        {
            var canvas = renderManager.UiCanvasSize;
            var p = renderManager.UiToCanvas(inputManager.Mouse.Position);
            var value = InventoryPanelLayout.OffsetForThumbTop(
                p.Y - grab, inventoryView.ThumbDragRowCount, canvas);
            SetListScroll(value, !inventoryView.Search.IsEmpty);
        }

        // The [E] panel owns the mouse: clicks equip, hotbar keys rest, and the world runs on.
        // While a text field has focus, E is typing, not a close.
        var ePressed = inputManager.Keyboard?.IsKeyPressed(Key.E) == true;
        if (ePressed && !eWasPressed && !pauseMenuView.IsOpen
            && inventoryView.Focus == InventoryFocus.None)
            SetInventoryOpen(!inventoryView.IsOpen);
        eWasPressed = ePressed;

        if (!inventoryView.IsOpen && !pauseMenuView.IsOpen)
        {
            // 1-10 grabs the hotbar slot directly; scroll still walks it one step at a time
            for (var i = 0; i < NumberKeys.Length; i++)
                if (inputManager.Keyboard?.IsKeyPressed(NumberKeys[i]) == true)
                    w.Inventory.Select(i);

            // One throw per press, so holding Q doesn't empty the slot; Ctrl+Q throws the stack
            var qPressed = inputManager.Keyboard?.IsKeyPressed(Key.Q) == true;
            if (qPressed && !qWasPressed)
            {
                var wholeStack = inputManager.Keyboard?.IsKeyPressed(Key.ControlLeft) == true
                    || inputManager.Keyboard?.IsKeyPressed(Key.ControlRight) == true;
                w.ThrowSelected(wholeStack);
            }
            qWasPressed = qPressed;
        }
        FrameProfiler.End(ProfileSection.ProgramUpdate);

        // Right-click use/place does real work (raycast plus a synchronous remesh on bucket
        // pours and single-block placements), so it gets its own span instead of hiding in
        // the input shell. The block interaction always runs first, on the press edge and then
        // on the repeat timer; a throwable only starts charging when nothing claimed the click,
        // so a mudball cooks on a lit campfire instead of sailing over it. Hold to charge,
        // release to throw.
        var rightDown = inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true;
        var rightHeld = rightDown && !inventoryView.IsOpen && !pauseMenuView.IsOpen;
        var rightPressed = rightHeld && !rightWasDown;
        if (rightHeld && w.IsThrowCharging)
        {
            w.UpdateThrowCharge((float)deltaTime);
        }
        else if (rightHeld && (rightPressed || placeTimer >= MAX_PLACE_TIMER))
        {
            placeTimer = 0;
            FrameProfiler.Begin(ProfileSection.UsePlace);
            var claimed = w.TryUseSelected(World.InteractionReach);
            FrameProfiler.End(ProfileSection.UsePlace);
            if (!claimed)
                w.TryBeginThrowCharge();
        }
        else if (!rightHeld && w.IsThrowCharging)
        {
            FrameProfiler.Begin(ProfileSection.UsePlace);
            w.ReleaseThrow();
            placeTimer = 0;
            FrameProfiler.End(ProfileSection.UsePlace);
        }
        rightWasDown = rightDown;
        if (!rightHeld && (inventoryView.IsOpen || pauseMenuView.IsOpen) && w.IsThrowCharging)
            w.CancelThrowCharge();

        // The menu freezes the simulation: the world keeps rendering behind the overlay
        // but physics, chunks, fluids and mining all hold still until resume.
        // The update below times itself; the shell spans above and below stay outside it.
        if (!pauseMenuView.IsOpen)
        {
            // A focused text field owns the keyboard: pass no board so the camera cannot act on
            // the very keys being typed (E, C, Space, WASD) while the panel is up.
            var board = inventoryView.IsOpen && inventoryView.Focus != InventoryFocus.None
                ? null
                : inputManager.Keyboard;
            w.Update((float)deltaTime, board);

            FrameProfiler.Begin(ProfileSection.Breaking);
            w.UpdateBlockBreaking(deltaTime,
                !inventoryView.IsOpen && inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);
            FrameProfiler.End(ProfileSection.Breaking);

            var middlePressed = !inventoryView.IsOpen
                && inputManager.Mouse?.IsButtonPressed(MouseButton.Middle) == true;
            if (middlePressed && !middleWasPressed)
                w.TryPickTargetedBlock();
            middleWasPressed = middlePressed;
        }
        else
        {
            middleWasPressed = false;
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
        // Only a playing world has a panel; while loading, SuppressLook belongs to the World
        if (world is not { IsPlaying: true }) return;
        inventoryView.IsOpen = open;
        if (!open)
        {
            inventoryView.ClearSelection();
            inventoryView.ClearInput();
            inventoryView.Craft.Clear();
            inventoryView.EndThumbDrag();
        }
        world.Camera.SuppressLook = open;
        if (inputManager.Mouse != null)
            inputManager.Mouse.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
    }

    private static void SetPauseMenu(PauseMenuPage page)
    {
        // Only a playing world can be paused; while loading, SuppressLook belongs to the World
        if (world is not { IsPlaying: true }) return;
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

    /// <summary>
    /// A slider drag tracks the cursor every frame until release (which saves), on whichever
    /// menu's settings page is open (<see cref="SettingsPageOpen"/>).
    /// </summary>
    private static void TrackSettingsDrag()
    {
        if (!SettingsPageOpen || !settingsMenuView.DragRow.HasValue
            || SettingsRows.All[settingsMenuView.DragRow.Value] is not SliderRow drag
            || inputManager.Mouse == null)
            return;

        var tabRow = SettingsLayout.TabRowOf(settingsMenuView.Tab, settingsMenuView.DragRow.Value);
        if (tabRow < 0 || !inputManager.Mouse.IsButtonPressed(MouseButton.Left))
        {
            settingsMenuView.DragRow = null;
            GameSettings.Save();
        }
        else
        {
            var canvas = renderManager.UiCanvasSize;
            drag.Set(SettingsLayout.SliderValue(drag,
                SettingsLayout.SliderFraction(renderManager.UiToCanvas(inputManager.Mouse.Position).X, tabRow, canvas)));
        }
    }

    private static void HandleSettingsClick(System.Numerics.Vector2 position)
    {
        var hit = renderManager.UiSettingsHitAt(position, settingsMenuView);
        switch (hit.Kind)
        {
            case SettingsHitKind.Back:
                if (screen == AppScreen.Menu)
                    menu.CloseSettings();
                else
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
                // SAVE & QUIT: the saving frame first, the teardown on the next update
                RequestSaveAndQuit();
                break;
            case PauseMenuHit.Back:
                SetPauseMenu(PauseMenuPage.Main);
                break;
        }
    }

    /// <summary>Number of list rows the given panel tab shows this frame. In creative the
    /// Blocks/Tools/Misc tabs list the whole catalog; the Craft tab always lists every recipe.</summary>
    private static int RowCountFor(InventoryCategory tab)
    {
        if (tab == InventoryCategory.Crafting)
            return ContentRegistry.CraftingRecipes.Count;
        if (world?.Camera.IsCreative == true)
            return CreativeCatalog.Entries(tab, world.Inventory).Count;
        return tab == InventoryCategory.Tools
            ? world!.Inventory.ToolEntries().Count
            : world!.Inventory.StackEntries(tab).Count;
    }

    /// <summary>
    /// A left press on the open panel's scrollbar: the thumb starts a drag (the grab offset
    /// and row count are latched so the per-frame drag never re-sorts the list), the track
    /// above or below it pages by one viewport.
    /// </summary>
    private static void HandleInventoryScrollbarPress(System.Numerics.Vector2 position)
    {
        var canvas = renderManager.UiCanvasSize;
        var p = renderManager.UiToCanvas(position);
        var searching = !inventoryView.Search.IsEmpty;
        var rowCount = searching
            ? InventorySearch.Search(inventoryView.Search.Query, world!.Inventory, world.Camera.IsCreative).Count
            : RowCountFor(inventoryView.Tab);
        var offset = searching
            ? InventoryPanelLayout.ClampedOffset(inventoryView.Search.ScrollOffset, rowCount, canvas)
            : InventoryPanelLayout.ClampedOffset(inventoryView.ScrollOffset, rowCount, canvas);
        switch (InventoryPanelLayout.ScrollbarHitAt(p, canvas, offset, rowCount))
        {
            case ScrollbarHit.Thumb:
                var thumb = InventoryPanelLayout.ThumbRect(canvas, offset, rowCount);
                inventoryView.ThumbDragGrab = p.Y - (thumb.Centre.Y - thumb.Size.Y / 2f);
                inventoryView.ThumbDragRowCount = rowCount;
                break;
            case ScrollbarHit.TrackAbove:
                SetListScroll(InventoryPanelLayout.ClampedOffset(
                    offset - InventoryPanelLayout.ListHeight(canvas), rowCount, canvas), searching);
                break;
            case ScrollbarHit.TrackBelow:
                SetListScroll(InventoryPanelLayout.ClampedOffset(
                    offset + InventoryPanelLayout.ListHeight(canvas), rowCount, canvas), searching);
                break;
        }
    }

    /// <summary>Types one printable character into the focused inventory field.</summary>
    private static void HandleInventoryType(char c)
    {
        if (inventoryView.Focus != InventoryFocus.Search) return;
        if (!inventoryView.Search.Text.TryAppend(c)) return;
        inventoryView.Search.Highlight = -1;
        inventoryView.Search.ScrollOffset = 0f;
    }

    /// <summary>The non-character keys a focused inventory field reacts to. ESC is left to the
    /// polled panel toggle, so one press never does two things.</summary>
    private static void HandleInventoryKey(Key key)
    {
        switch (key)
        {
            case Key.Backspace:
                if (inventoryView.Focus != InventoryFocus.Search) break;
                inventoryView.Search.Text.Backspace();
                inventoryView.Search.Highlight = -1;
                break;
            case Key.Tab:
                inventoryView.Focus = InventoryFocus.Search;
                break;
            case Key.Enter or Key.KeypadEnter:
                ApplyHighlightedSearchHit();
                break;
            case Key.Up:
                MoveSearchHighlight(-1);
                break;
            case Key.Down:
                MoveSearchHighlight(1);
                break;
        }
    }

    private static void MoveSearchHighlight(int delta)
    {
        if (inventoryView.Search.IsEmpty) return;
        var count = InventorySearch.Search(inventoryView.Search.Query, world!.Inventory).Count;
        if (count == 0) return;
        inventoryView.Search.Highlight = inventoryView.Search.Highlight < 0
            ? (delta > 0 ? 0 : count - 1)
            : Math.Clamp(inventoryView.Search.Highlight + delta, 0, count - 1);
        EnsureSearchHighlightVisible(count);
    }

    private static void EnsureSearchHighlightVisible(int count)
    {
        var screen = renderManager.UiCanvasSize;
        var viewport = InventoryPanelLayout.ListHeight(screen);
        var rowTop = inventoryView.Search.Highlight * InventoryPanelLayout.RowHeight;
        var offset = inventoryView.Search.ScrollOffset;
        if (rowTop < offset)
            offset = rowTop;
        else if (rowTop + InventoryPanelLayout.RowHeight > offset + viewport)
            offset = rowTop + InventoryPanelLayout.RowHeight - viewport;
        inventoryView.Search.ScrollOffset = InventoryPanelLayout.ClampedOffset(offset, count, screen);
    }

    private static void ApplyHighlightedSearchHit()
    {
        if (inventoryView.Search.IsEmpty) return;
        var hits = InventorySearch.Search(inventoryView.Search.Query, world!.Inventory);
        if (hits.Count == 0) return;
        ApplySearchHit(hits[inventoryView.Search.Highlight < 0 ? 0 : inventoryView.Search.Highlight]);
    }

    /// <summary>
    /// Acts on one search result: an owned item becomes the panel's pick (a recipe that is open
    /// is fed it), while a recipe opens on the Craft tab and clears the query.
    /// </summary>
    private static void ApplySearchHit(SearchHit hit)
    {
        if (hit.IsRecipe)
        {
            inventoryView.Tab = InventoryCategory.Crafting;
            inventoryView.Craft.Select(hit.Recipe);
            inventoryView.CraftScrollOffset = 0f;
            inventoryView.Search.Clear();
            inventoryView.Focus = InventoryFocus.None;
            return;
        }

        if (world!.Camera.IsCreative)
        {
            GrantFromCreative(hit.Id, 1);
            return;
        }

        inventoryView.SelectedId = hit.ToolInstance > 0 ? (byte)0 : hit.Id;
        inventoryView.SelectedToolInstance = hit.ToolInstance;
        if (hit.ToolInstance == 0 && inventoryView.Craft.Recipe != null)
            inventoryView.Craft.FeedFromInventory(hit.Id, world.Inventory);
    }

    /// <summary>Grants a catalog id to the player, popping the hotbar slot a fresh pickup lands
    /// in; the catalog's +1/+10/+100 buttons and the row body go through here.</summary>
    private static void GrantFromCreative(byte id, int amount)
    {
        var inventory = world!.Inventory;
        if (!inventory.Add(id, amount)) return;
        if (Inventory.CategoryOf(id) is InventoryCategory.Blocks or InventoryCategory.Misc)
            inventoryView.SelectedId = id;
    }

    /// <summary>
    /// Acts on one creative catalog row click: a button grants its amount or clears the id's
    /// counts, the row body grants one.
    /// </summary>
    private static void ApplyCatalogButton(byte id, int row, float offset, int rowCount,
        System.Numerics.Vector2 position)
    {
        var button = InventoryPanelLayout.CatalogButtonAt(renderManager.UiToCanvas(position),
            row, offset, rowCount, renderManager.UiCanvasSize);
        switch (button)
        {
            case InventoryPanelLayout.CatalogButton.AddOne:
                GrantFromCreative(id, 1);
                break;
            case InventoryPanelLayout.CatalogButton.AddTen:
                GrantFromCreative(id, 10);
                break;
            case InventoryPanelLayout.CatalogButton.AddHundred:
                GrantFromCreative(id, 100);
                break;
            case InventoryPanelLayout.CatalogButton.Clear:
                if (world!.Inventory.RemoveAll(id))
                    inventoryView.ClearSelection();
                break;
            default:
                GrantFromCreative(id, 1);
                break;
        }
    }

    private static void SetListScroll(float offset, bool searching)
    {
        if (searching) inventoryView.Search.ScrollOffset = offset;
        else inventoryView.ScrollOffset = offset;
    }

    /// <summary>
    /// Routes one left-click inside the open panel: the field boxes focus, tabs switch, rows
    /// pick for preview (or act on a search result), crafting controls feed the open recipe, and
    /// hotbar slots equip the pick (clicking what the slot already shows clears it).
    /// </summary>
    private static void HandleInventoryClick(System.Numerics.Vector2 position)
    {
        var inventory = world!.Inventory;
        var creative = world.Camera.IsCreative;

        // The search box takes the click before anything else; clicking elsewhere drops focus.
        if (renderManager.UiSearchBoxAt(position))
        {
            inventoryView.Focus = InventoryFocus.Search;
            return;
        }
        inventoryView.Focus = InventoryFocus.None;

        var tab = renderManager.UiPanelTabAt(position);
        if (tab.HasValue)
        {
            inventoryView.Tab = tab.Value;
            inventoryView.Search.Clear();
            inventoryView.ClearSelection();
            return;
        }

        var searching = !inventoryView.Search.IsEmpty;
        IReadOnlyList<SearchHit>? hits = searching
            ? InventorySearch.Search(inventoryView.Search.Query, inventory, creative)
            : null;
        var rowCount = hits?.Count ?? RowCountFor(inventoryView.Tab);
        var offset = searching
            ? InventoryPanelLayout.ClampedOffset(inventoryView.Search.ScrollOffset, rowCount, renderManager.UiCanvasSize)
            : InventoryPanelLayout.ClampedOffset(inventoryView.ScrollOffset, rowCount, renderManager.UiCanvasSize);
        // The scrollbar is handled on press; its release must not fall through to the
        // crafting card or hotbar tests below.
        if (InventoryPanelLayout.ScrollbarHitAt(renderManager.UiToCanvas(position),
                renderManager.UiCanvasSize, offset, rowCount) != ScrollbarHit.None)
            return;
        var row = renderManager.UiPanelRowAt(position, offset, rowCount);
        if (row.HasValue)
        {
            if (hits != null)
            {
                if (row.Value < hits.Count)
                    ApplySearchHit(hits[row.Value]);
            }
            else if (creative && inventoryView.Tab != InventoryCategory.Crafting)
            {
                var entries = CreativeCatalog.Entries(inventoryView.Tab, inventory);
                if (row.Value < entries.Count)
                    ApplyCatalogButton(entries[row.Value].Id, row.Value, offset, rowCount, position);
            }
            else if (inventoryView.Tab == InventoryCategory.Tools)
            {
                inventoryView.SelectedToolInstance = inventory.ToolEntries()[row.Value].InstanceId;
                inventoryView.SelectedId = 0;
            }
            else if (inventoryView.Tab == InventoryCategory.Crafting)
            {
                inventoryView.Craft.Select(ContentRegistry.CraftingRecipes[row.Value]);
                // A newly opened recipe starts at the top of its card.
                inventoryView.CraftScrollOffset = 0f;
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
        // Menu, loading and saving frames draw no world at all: one flat UI pass into the
        // default framebuffer, no HDR target and no GPU timer frame (so none is closed either).
        // Presenting this frame is what releases pending work to the next update.
        if (screen != AppScreen.Playing)
        {
            FrameProfiler.Begin(ProfileSection.RenderShell);
            renderManager.RenderMenuFrame(windowManager.Window,
                screen == AppScreen.Menu ? menu.View : null, settingsMenuView,
                screen == AppScreen.Menu ? null : loadingView, windowManager.Window.Time);
            ambience?.Update(renderManager.Dayness, (float)MathUtils.Clamp(deltaTime, 0.0, 0.25));
            sfx?.Update();
            if (pending != PendingWork.None)
                pendingFramePresented = true;
            FrameProfiler.End(ProfileSection.RenderShell);
            FrameProfiler.EndFrame();
            return;
        }

        var world = Program.world!;

        // Prepares this frame's GPU timers and latches the timing gate before any draw: no query is
        // opened here (each pass brackets its own), and the header's gpu figure is the sum of the
        // per-pass averages. debugEnabled is the live X-overlay state and this is the render thread,
        // so the gate reads it directly.
        FrameProfiler.Begin(ProfileSection.RenderShell);
        renderManager.BeginGpuFrame(debugEnabled);
        FrameProfiler.End(ProfileSection.RenderShell);

        FrameProfiler.Begin(ProfileSection.Lighting);
        renderManager.BeginGpuPass(ProfileSection.Lighting);
        renderManager.SetLighting(world.LightingTime, world.Camera, world.PrimedTnt,
            world.Camera.IsEyeInWater, world.Camera.IsEyeInLava, world.FireCells);
        ambience?.Update(renderManager.Dayness, (float)MathUtils.Clamp(deltaTime, 0.0, 0.25));
        sfx?.Update();
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
                (float)world.LightingTime, shownFps, debugEnabled,
                showCoordinates: true, menu: pauseMenuView, settings: settingsMenuView);
        // The loading screen fades out over the world's first playing frames
        if (!loadingView.IsFinished)
            renderManager.RenderLoadingOverlay(windowManager.Window, loadingView, windowManager.Window.Time);
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
        // A saved world is saved on close exactly like SAVE & QUIT (no frame to announce it;
        // GL is still current here). A storeless dev-override world is left to process exit.
        if (world != null && currentWorld != null)
        {
            TryTouch(currentWorld);
            world.SaveAndDispose(renderManager);
            world = null;
            currentWorld = null;
        }

        ambience?.Dispose();
        sfx?.Dispose();
        // The last scripted step is usually a screenshot whose encode is still on a
        // worker; drain it before tearing down GL and the log queue.
        VisualCapture.WaitForPending();
        renderManager?.Dispose();
        inputManager?.Dispose();
        Log.Shutdown();
    }
}