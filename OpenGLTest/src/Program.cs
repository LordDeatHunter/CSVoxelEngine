using System;
using System.IO;
using System.Runtime;
using Silk.NET.Input;
using Silk.NET.Maths;
using VoxelEngine.App;
using VoxelEngine.Audio;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.Saves;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;
using VoxelEngine.VisualTest;

namespace VoxelEngine;

/// <summary>
/// Entry point and window wiring. The game boots to the main menu (<see cref="MainMenuController"/>)
/// unless a dev override is set (<see cref="LaunchMode.BypassesMenu"/>), in which case it builds a
/// storeless <see cref="World"/> straight away exactly as before. PLAY/CREATE and the pause menu's
/// third button, SAVE &amp; QUIT, are deferred one frame: the click only records the work, the next
/// render presents the loading or saving screen, and the update after that runs the blocking call.
/// </summary>
public static class Program
{
    /// <summary>Blocking work recorded by a click and run on the update after its screen was presented.</summary>
    private enum PendingWork { None, OpenWorld, SaveAndQuit, Connect }

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
    private static ConnectTarget pendingConnect;
    private static readonly InventoryView inventoryView = new();
    private static readonly ChatView chatView = new();
    private static readonly PauseMenuView pauseMenuView = new();
    private static readonly SettingsMenuView settingsMenuView = new();
    private static DevDiagnostics diagnostics = null!;
    private static ClientSession session = null!;
    private static InventoryPanelInput panelInput = null!;
    private static ChatInput chatInput = null!;
    private static MenuInput menuInput = null!;
    private static GameplayInput gameplay = null!;
    private static InputEvents inputEvents = null!;
    private static double fpsTimer;
    private static int fpsFrames;
    private static int shownFps;

    // LaunchMode.ScriptVariable (VOXEL_SCRIPT) names a scenario JSON to run a scripted visual test
    // instead of playing: teleport/walk/mine/screenshot steps drive the real update functions, then
    // the window closes. A bad path or bad script fails fast instead of trapping anyone.

    // VOXEL_TIME: seeds World.TimeOffset once after construction, so a screenshot can be taken at
    // any point of the day without waiting for the sun to get there. The World owns the lighting
    // clock from then on (World.LightingTime), and a restored world overrides this offset. A world
    // opened from the menu is rebased by the window clock at construction, so it starts at the
    // same time of day however long the menu was open.
    private static double timeOffset;

    /// <summary>The live world, null on the menu and while connecting. Read live by the
    /// components; only the client session sets it (it builds and drops the client world).</summary>
    internal static World? ActiveWorld
    {
        get => world;
        set => world = value;
    }

    /// <summary>The saved world being played; null on the menu, for a storeless dev-override world and for a client world.</summary>
    internal static WorldInfo? CurrentWorld => currentWorld;

    /// <summary>What the window is showing, read live by the input components.</summary>
    internal static AppScreen Screen => screen;

    public static void Main()
    {
        // PERF_TODO item 10: keep full blocking Gen2s off the frame path. SustainedLowLatency
        // defers them in favor of background collections; the startup line already logs the
        // effective latency mode via DevDiagnostics.PrintBuildDiagnostics, so a perf capture records it.
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
        DevDiagnostics.PrintBuildDiagnostics();
        timeOffset = DevDiagnostics.ReadTimeOffset();

        inputManager = new InputManager(windowManager.Window);
        renderManager = new RenderManager(windowManager.Window);
        sfx = new SfxManager();
        worldStore = new WorldStore(WorldStore.DefaultRoot);
        serverStore = new ServerStore(ServerStore.DefaultPath);
        menu = new MainMenuController(worldStore, serverStore, Random.Shared.Next);

        // The app's components, built once; they read the live world and screen through Program
        diagnostics = new DevDiagnostics();
        session = new ClientSession(windowManager, inputManager, renderManager, sfx, loadingView, chatView, diagnostics);
        panelInput = new InventoryPanelInput(inputManager, renderManager, inventoryView, pauseMenuView, session);
        chatInput = new ChatInput(chatView, session);
        menuInput = new MenuInput(windowManager, inputManager, renderManager, menu, pauseMenuView, settingsMenuView, panelInput, inventoryView);
        gameplay = new GameplayInput(inputManager, renderManager, inventoryView, chatView, pauseMenuView, panelInput, menuInput, session, diagnostics);
        inputEvents = new InputEvents(inputManager, renderManager, menu, inventoryView, chatView, pauseMenuView, settingsMenuView, panelInput, menuInput, chatInput, gameplay);

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
            devWorld.Player.Rig = renderManager.Players.PlayerRig;
            // Entity draws sample their light from the live chunks from here on
            renderManager.LightSource = devWorld.GetChunkManager();
            screen = AppScreen.Playing;
        }

        inputManager.Mouse?.MouseUp += (_, button) => inputEvents.OnMouseUp(button);

        inputManager.Mouse?.MouseDown += (_, button) => inputEvents.OnMouseDown(button);

        inputManager.Mouse?.Scroll += (_, wheel) => inputEvents.OnScroll(wheel);

        // Menu text input: characters through KeyChar, the four control keys through KeyDown. Both
        // act only while the main menu is showing, and ESC for the menu lives here alone (the
        // polled ESC below is world-only), so one press never makes two transitions. While a world
        // plays, the same events feed the [E] panel's search field instead.
        if (inputManager.Keyboard is { } keyboard)
        {
            keyboard.KeyChar += (_, c) => inputEvents.OnKeyChar(c);
            keyboard.KeyDown += (_, key, _) => inputEvents.OnKeyDown(key);
        }

        ambience = new AmbienceManager();

        diagnostics.DebugEnabled = Environment.GetEnvironmentVariable(DevDiagnostics.DebugVariable)?.Trim() is "1" or "true";

        if (!bypassMenu)
        {
            ShowMenu();
            return;
        }

        var script = Environment.GetEnvironmentVariable(LaunchMode.ScriptVariable);
        if (string.IsNullOrWhiteSpace(script)) return;

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

    /// <summary>Shows the main menu's first page with a free cursor.</summary>
    private static void ShowMenu()
    {
        screen = AppScreen.Menu;
        menu.OpenMain();
        if (inputManager.Mouse != null)
            inputManager.Mouse.Cursor.CursorMode = CursorMode.Normal;
    }

    /// <summary>Acts on what a main-menu click or key asked for.</summary>
    internal static void HandleMenuAction(MainMenuAction action)
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
                    RequestConnect(connect);
                break;
            case MainMenuAction.Exit:
                windowManager.Window.Close();
                break;
        }
    }

    /// <summary>
    /// Records a server to connect to and switches to the connecting screen; the connection starts
    /// on the update after that screen was presented, exactly like a world open.
    /// </summary>
    private static void RequestConnect(ConnectTarget target)
    {
        pending = PendingWork.Connect;
        pendingConnect = target;
        pendingFramePresented = false;
        loadingView.Begin(LoadingScreenKind.Loading, target.Name, "Connecting...");
        screen = AppScreen.Loading;
    }

    internal static void AbortConnect(string notice)
    {
        loadingView.Finish();
        ShowMenu();
        menu.OpenMultiplayer();
        menu.ShowNotice(notice);
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
    internal static void RequestSaveAndQuit()
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
            case PendingWork.Connect:
                session.BeginConnect(pendingConnect);
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
        opened.Player.Rig = renderManager.Players.PlayerRig;
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
        // A multiplayer client leaves the server and returns to the server list; a singleplayer
        // world saves and returns to the worlds page.
        var wasMultiplayer = session.Client != null;
        session.DisconnectNetwork();
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
        if (wasMultiplayer)
        {
            menu.OpenMultiplayer();
            return;
        }
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
    internal static void ResetWorldViews()
    {
        pauseMenuView.Page = PauseMenuPage.Closed;
        settingsMenuView.DragRow = null;
        inventoryView.ResetForNewWorld();
        chatView.Reset();
        gameplay.ResetEdges();
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
            diagnostics.LogDebugLine(deltaTime, world);
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

        switch (screen)
        {
            case AppScreen.Menu:
                FrameProfiler.Begin(ProfileSection.ProgramUpdate);
                menuInput.SyncWindowSettings();
                menuInput.TrackSettingsDrag();
                FrameProfiler.End(ProfileSection.ProgramUpdate);
                return;
            case AppScreen.Loading:
            {
                FrameProfiler.Begin(ProfileSection.ProgramUpdate);
                menuInput.SyncWindowSettings();
                FrameProfiler.End(ProfileSection.ProgramUpdate);
                // Connecting to a server: no world exists yet; the handshake builds it.
                if (world == null)
                {
                    session.UpdateConnecting((float)deltaTime);
                    return;
                }

                // The world streams on its own raised budgets; no key, mouse, pause menu or panel
                // reaches it, so Camera.SuppressLook stays the World's until its Playing flip.
                var loading = world;
                loading.Update((float)deltaTime, inputManager.Keyboard);
                loadingView.Update(loading.LoadProgress, loading.IsLoaded, (float)deltaTime);
                if (loading.IsLoaded)
                    screen = AppScreen.Playing;
                FrameProfiler.Begin(ProfileSection.DebugLog);
                diagnostics.LogDebugLine(deltaTime, world);
                FrameProfiler.End(ProfileSection.DebugLog);
                return;
            }
        }

        if (screen != AppScreen.Playing) return;
        var w = world!;
        switch (session.Client)
        {
            // The server connection, if any: send our state and apply what it broadcast.
            case { Status: NetworkStatus.Handshaken } connectedClient when !session.UpdateNetwork(connectedClient, (float)deltaTime):
                return;
            case { Status: NetworkStatus.Disconnected or NetworkStatus.Failed } lostClient:
                lostClient.Dispose();
                session.DisconnectToMenu("Lost connection to the server");
                return;
        }
        // The loading overlay fades out over the first playing frames
        if (!loadingView.IsFinished)
            loadingView.Update(1f, true, (float)deltaTime);

        gameplay.Update(w, deltaTime);

        FrameProfiler.Begin(ProfileSection.DebugLog);
        diagnostics.LogDebugLine(deltaTime, world);
        FrameProfiler.End(ProfileSection.DebugLog);
    }

    private static void OnRender(double deltaTime)
    {
        // Menu, loading and saving frames draw no world at all: one flat UI pass into the
        // default framebuffer, no HDR target and no GPU timer frame (so none is closed either).
        // Presenting this frame is what releases pending work to the next update.
        if (screen != AppScreen.Playing)
        {
            FrameProfiler.Begin(ProfileSection.RenderShell);
            renderManager.UiPasses.RenderMenuFrame(windowManager.Window,
                screen == AppScreen.Menu ? menu.View : null, settingsMenuView,
                screen == AppScreen.Menu ? null : loadingView, windowManager.Window.Time);
            ambience?.Update(renderManager.Lighting.Dayness, (float)MathUtils.Clamp(deltaTime, 0.0, 0.25));
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
        // per-pass averages. DevDiagnostics.DebugEnabled is the live X-overlay state and this is the render thread,
        // so the gate reads it directly.
        FrameProfiler.Begin(ProfileSection.RenderShell);
        renderManager.GpuTimer.BeginGpuFrame(diagnostics.DebugEnabled);
        FrameProfiler.End(ProfileSection.RenderShell);

        FrameProfiler.Begin(ProfileSection.Lighting);
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.Lighting);
        renderManager.Lighting.SetLighting(world.LightingTime, world.Camera, world.PrimedTnt,
            world.Camera.IsEyeInWater, world.Camera.IsEyeInLava, world.FireCells);
        ambience?.Update(renderManager.Lighting.Dayness, (float)MathUtils.Clamp(deltaTime, 0.0, 0.25));
        sfx?.Update();
        renderManager.BeginFrame(world.Camera);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.Lighting);
        FrameProfiler.End(ProfileSection.Lighting);

        world.Renderer.Render(renderManager);

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
        renderManager.GpuTimer.BeginGpuPass(ProfileSection.UiPass);
        var hideHud = !gameplay.HudVisible
            || (visualDirector?.TakeScreenshot == true && visualDirector.ScreenshotHideHud);
        if (!hideHud)
            world.Renderer.RenderUI(renderManager, inventoryView,
                (float)world.LightingTime, shownFps, diagnostics.DebugEnabled,
                showCoordinates: true, menu: pauseMenuView, settings: settingsMenuView);
        // The chat log and input draw over the HUD; hidden with the HUD and behind the pause menu.
        if (!hideHud && !pauseMenuView.IsOpen)
            renderManager.UiPasses.RenderChat(windowManager.Window, chatView, windowManager.Window.Time);
        // The Tab player list rides over the HUD while the key is held (a server session only)
        if (!hideHud && !pauseMenuView.IsOpen && gameplay.TabHeld && session.Client != null)
            renderManager.UiPasses.RenderPlayerList(windowManager.Window, chatInput.PlayerListNames());
        // A hover tooltip over the [E] panel's rows and the hotbar slots
        if (!hideHud && !pauseMenuView.IsOpen)
            renderManager.UiPasses.RenderTooltip(windowManager.Window, panelInput.HoveredTooltip());
        // The death screen covers the HUD and the world until the player asks to respawn; a
        // client has no death line (the server's chat carries it)
        if (!hideHud && gameplay.DeathScreenUp(world))
            renderManager.UiPasses.RenderDeathScreen(windowManager.Window,
                session.Client == null ? world.LocalPlayer.DeathLine : null);
        // The loading screen fades out over the world's first playing frames
        if (!loadingView.IsFinished)
            renderManager.UiPasses.RenderLoadingOverlay(windowManager.Window, loadingView, windowManager.Window.Time);
        renderManager.GpuTimer.EndGpuPass(ProfileSection.UiPass);
        FrameProfiler.End(ProfileSection.UiPass);

        // Timed even when no screenshot is due, so the pairing stays obvious; an empty span
        // costs one Stopwatch read.
        FrameProfiler.Begin(ProfileSection.Screenshot);
        if (visualDirector?.TakeScreenshot == true || gameplay.ScreenshotRequested)
        {
            var scripted = visualDirector?.TakeScreenshot == true;
            var path = scripted ? visualDirector!.ScreenshotPath : Path.Combine(ScreenshotPaths.Directory,
                ScreenshotPaths.NameFor(DateTime.Now));
            if (scripted) visualDirector!.TakeScreenshot = false;
            else gameplay.ScreenshotRequested = false;
            var size = windowManager.Window.FramebufferSize;
            var pixels = VisualCapture.ReadPixels(renderManager.gl, size.X, size.Y);
            VisualCapture.QueueSave(pixels, size.X, size.Y, path);
        }
        FrameProfiler.End(ProfileSection.Screenshot);

        FrameProfiler.Begin(ProfileSection.RenderShell);
        renderManager.GpuTimer.EndGpuFrame();
        FrameProfiler.End(ProfileSection.RenderShell);
        FrameProfiler.EndFrame();
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        renderManager.SetViewport(size);
    }

    private static void OnClosing()
    {
        // A multiplayer client drops its connection; the server owns the save, not the client.
        session?.DisconnectNetwork();
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