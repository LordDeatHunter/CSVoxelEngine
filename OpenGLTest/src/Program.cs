using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime;
using System.Threading;
using Silk.NET.Input;
using Silk.NET.Maths;
using VoxelEngine.Audio;
using VoxelEngine.Content;
using VoxelEngine.Interaction;
using VoxelEngine.Networking;
using VoxelEngine.Platform;
using VoxelEngine.Player;
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
    /// <summary>What the window is showing: a menu, a world streaming in, a world, or a teardown.</summary>
    private enum AppScreen { Menu, Loading, Playing, Saving }

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
    // The live multiplayer connection, null in singleplayer and on the menu
    private static NetworkClient? netClient;
    private static ClientChunkStreamer? clientStreamer;
    private static ClientLodStreamer? clientLodStreamer;
    private static ConnectTarget pendingConnect;
    private static int netPlayerId = -1;
    private static readonly List<RemotePlayerSnapshot> remoteScratch = new();
    private static readonly List<NetEntity> netEntitiesScratch = new();
    private static readonly List<NetCellChange> netCellsScratch = new();
    // The server values a message lets through to the world (the rest wait behind a prediction)
    private static readonly List<NetCellChange> netApplyScratch = new();
    // What this client predicted and the server has not answered yet; one per connection
    private static ClientPrediction prediction = new();
    private static double netStateTimer;
    private static double netKeepAliveTimer;
    // The sequence id of the newest PlayerCorrection applied, echoed in every PlayerState so the
    // server knows the states it reads were sent from the corrected position
    private static int netCorrectionAck;
    // Whether the server's status says this player is dead, and the time since the last Respawn
    // intent sent while it does
    private static bool netDead;
    private static float netRespawnTimer;
    private static bool debugEnabled;
    private static bool hudVisible = true;
    private static bool screenshotRequested;
    private static bool f1WasPressed;
    private static bool f2WasPressed;
    private static bool tabHeld;
    private static bool xWasPressed;
    private static bool qWasPressed;
    private static bool rightWasDown;
    private static bool middleWasPressed;
    private static bool eWasPressed;
    private static bool tWasPressed;
    private static bool escWasPressed;
    private static readonly InventoryView inventoryView = new();
    private static readonly ChatView chatView = new();
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
        // defers them in favor of background collections; the startup line already logs the
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
            switch (button)
            {
                case MouseButton.Right:
                    placeTimer = MAX_PLACE_TIMER;
                    break;
                case MouseButton.Left when inventoryView.IsOpen && inputManager.Mouse != null:
                {
                    // Releasing a thumb drag ends it here and nowhere else while the panel is
                    // open, so the release never equips or picks whatever is under the cursor.
                    if (inventoryView.ThumbDragGrab.HasValue)
                    {
                        inventoryView.EndThumbDrag();
                        return;
                    }
                    HandleInventoryClick(inputManager.Mouse.Position);
                    break;
                }
            }
        };

        inputManager.Mouse?.MouseDown += (_, button) =>
        {
            switch (screen)
            {
                case AppScreen.Loading or AppScreen.Saving:
                    return;
                case AppScreen.Playing when inventoryView.IsOpen && !pauseMenuView.IsOpen
                                                                 && button == MouseButton.Left && inputManager.Mouse != null:
                    HandleInventoryScrollbarPress(inputManager.Mouse.Position);
                    return;
            }

            if (!SettingsPageOpen || button != MouseButton.Left || inputManager.Mouse == null)
                return;
            var hit = renderManager.UiSettingsHitAt(inputManager.Mouse.Position, settingsMenuView);
            if (hit.Kind != SettingsHitKind.Slider || SettingsRows.All[hit.Row] is not SliderRow slider) return;
            settingsMenuView.DragRow = hit.Row;
            slider.Set(SettingsLayout.SliderValue(slider, hit.Fraction));
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

            if (chatView.IsOpen) return;
            switch (inventoryView.IsOpen)
            {
                case false when !pauseMenuView.IsOpen:
                    w.Inventory.Scroll(wheel.Y);
                    // While connected the server owns the selection: mirror the step as an intent, or
                    // the next inventory sync would put the slot straight back.
                    MirrorSelection(w.Inventory.SelectedIndex);
                    break;
                case true when !pauseMenuView.IsOpen && inputManager.Mouse != null:
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

                    break;
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
                switch (screen)
                {
                    case AppScreen.Menu:
                        menu.Type(c);
                        break;
                    case AppScreen.Playing when chatView.IsOpen && !pauseMenuView.IsOpen:
                        chatView.Type(c);
                        break;
                    case AppScreen.Playing when inventoryView.IsOpen && !pauseMenuView.IsOpen:
                        HandleInventoryType(c);
                        break;
                }
            };
            keyboard.KeyDown += (_, key, _) =>
            {
                if (screen == AppScreen.Playing)
                {
                    if (chatView.IsOpen && !pauseMenuView.IsOpen)
                        HandleChatKey(key);
                    else if (inventoryView.IsOpen && !pauseMenuView.IsOpen)
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
                switch (menuKey)
                {
                    case null:
                        return;
                    // ESC leaves the settings page; a live slider drag is saved like a release would
                    case MenuKey.Escape when menu.View.Page == MainMenuPage.Settings:
                    {
                        if (settingsMenuView.DragRow.HasValue)
                            GameSettings.Save();
                        settingsMenuView.DragRow = null;
                        break;
                    }
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

    /// <summary>Starts the connect + handshake on a background thread; the loading screen polls it.</summary>
    private static void BeginConnect()
    {
        netClient?.Dispose();
        prediction = new ClientPrediction();
        netClient = new NetworkClient(pendingConnect.Address, chatView.LocalName,
            WorldGenerationConfig.NetConnectTimeoutSeconds);
        var client = netClient;
        new Thread(() => client.Connect()) { IsBackground = true, Name = "net-connect" }.Start();
    }

    /// <summary>
    /// The connecting screen's frame: waits for the handshake, then builds the client world at the
    /// server's spawn and lets the normal loading path take over. A failure returns to the server list
    /// with the reason.
    /// </summary>
    private static void UpdateConnecting(float deltaTime)
    {
        var client = netClient;
        if (client == null)
            return;

        // Read once: the connect and reader threads publish it concurrently. A server that closes
        // right after its hello reaches Disconnected before this screen ever saw Handshaken, which
        // is a failed connect too, not a reason to wait forever.
        var status = client.Status;
        if (status is NetworkStatus.Failed or NetworkStatus.Disconnected)
        {
            var reason = client.Error ?? (status == NetworkStatus.Disconnected
                ? "The server closed the connection"
                : "Could not connect");
            DisconnectNetwork();
            AbortConnect(reason);
            return;
        }

        if (status != NetworkStatus.Handshaken)
        {
            loadingView.Update(0f, false, deltaTime);
            return;
        }

        var server = client.Server!.Value;

        // The client derives nothing from the seed (terrain, water levels and tint all stream from
        // the server), so it needs neither WorldSeed.Apply nor the seed lock.
        try
        {
            // A multiplayer client renders, so it is a real world with the renderer's pools and the
            // window - but client mode: no worldgen (chunks stream from the server), no far LOD and
            // no local simulation, so a custom or premade server world is exactly what is shown. It
            // starts where the server says and sinks onto the streamed terrain there.
            world = new World(renderManager.gl, renderManager.OpaqueChunkGeometry,
                renderManager.TranslucentChunkGeometry, renderManager.LodGeometry,
                windowManager.Window, inputManager.InputContext, sfx, null,
                serverMode: false, clientMode: true, spawn: server.Spawn)
            {
                Player =
                {
                    Rig = renderManager.PlayerRig
                },
                // Every action is predicted locally and sent as an intent; the cells it changed are
                // tagged with that intent, so the server's answer to it is told from older messages
                BlockEdited = StagePredictedCell,
                IntentRaised = SendWorldIntent
            };
            renderManager.LightSource = world.GetChunkManager();
            var fb = windowManager.Window.FramebufferSize;
            if (fb.Y > 0)
                world.Camera.SetAspectRatio(fb.X / (float)fb.Y);

            clientStreamer = new ClientChunkStreamer(world.GetChunkManager(), SendChunkRequest, SendColumnRequest);
            clientLodStreamer = new ClientLodStreamer(world.GetLodManager(), SendLodRequest);
        }
        catch (Exception e)
        {
            Log.Warn($"Multiplayer: could not build the world: {e.Message}");
            DisconnectNetwork();
            AbortConnect("Could not start the world - see the console");
            return;
        }

        // A client follows the server's day clock from the hello.
        world.SetLightingTime(server.Time);
        netPlayerId = server.PlayerId;
        ResetWorldViews();
        loadingView.Begin(LoadingScreenKind.Loading, server.WorldName, "Loading world...");
        Log.WriteLine($"Multiplayer: connected to {client.Address} as '{chatView.LocalName}' (#{server.PlayerId}) on '{server.WorldName}'");
    }

    private static void AbortConnect(string notice)
    {
        loadingView.Finish();
        ShowMenu();
        menu.OpenMultiplayer();
        menu.ShowNotice(notice);
    }

    /// <summary>Sends periodics and drains every server message onto the world and the chat.
    /// False when a bad message dropped the connection and returned to the menu (the world is
    /// gone then, so the caller must stop the frame).</summary>
    private static bool UpdateNetwork(NetworkClient client, float deltaTime)
    {
        var w = world!;
        clientStreamer?.Update(w.Camera.Position, deltaTime);
        clientLodStreamer?.Update(w.Camera.Position, deltaTime);
        netStateTimer += deltaTime;
        if (netStateTimer >= WorldGenerationConfig.NetPlayerStateSeconds)
        {
            netStateTimer = 0;
            var camera = w.Camera;
            using var writer = new PacketWriter(NetMessage.PlayerState);
            writer.Write(camera.Position);
            writer.Write(camera.Yaw);
            writer.Write(camera.Pitch);
            // The modes ride along as a request: the server decides whether this player may be
            // creative (and with it, whether its catalog grants are accepted)
            // The water pose follows the body's depth like the local model: a wader walks, a floater
            // takes the surface float, and both it and a submerged swimmer swim
            var waterPhase = camera.IsFlying ? WaterPhase.Dry : camera.WaterPhase;
            var supported = camera.IsGrounded || camera.IsFlying || waterPhase >= WaterPhase.Float;
            writer.Write((byte)((camera.IsCreative ? NetPlayerFlags.Creative : NetPlayerFlags.None)
                | (camera.IsFlying ? NetPlayerFlags.Flying : NetPlayerFlags.None)
                | (camera.IsSneaking ? NetPlayerFlags.Sneaking : NetPlayerFlags.None)
                | (waterPhase >= WaterPhase.Float ? NetPlayerFlags.Swimming : NetPlayerFlags.None)
                | (waterPhase == WaterPhase.Float ? NetPlayerFlags.Floating : NetPlayerFlags.None)
                | (camera.IsSprinting ? NetPlayerFlags.Sprinting : NetPlayerFlags.None)
                | (!supported ? NetPlayerFlags.Airborne : NetPlayerFlags.None)));
            writer.Write(netCorrectionAck);
            client.Send(writer.ToArray());
        }

        // A dead player comes back only through a Respawn intent: the first goes out when the
        // status says so, and it is asked again while the server still reports the player dead
        if (netDead)
        {
            netRespawnTimer += deltaTime;
            if (netRespawnTimer >= WorldGenerationConfig.NetRespawnRetrySeconds)
                SendRespawn(w.Camera);
        }

        netKeepAliveTimer += deltaTime;
        if (netKeepAliveTimer >= WorldGenerationConfig.NetKeepAliveSeconds)
        {
            netKeepAliveTimer = 0;
            using var writer = new PacketWriter(NetMessage.KeepAlive);
            client.Send(writer.ToArray());
        }

        while (client.Inbound.TryDequeue(out var payload))
        {
            // A truncated or malformed message from the server must not crash the game: leave
            // the server and say why instead.
            try
            {
                HandleNetworkMessage(w, payload);
            }
            catch (Exception e) when (e is EndOfStreamException or IOException
                                          or InvalidDataException or FormatException
                                          or ArgumentException)
            {
                Log.Warn($"Multiplayer: bad message from the server: {e.Message}");
                DisconnectToMenu("The server sent bad data");
                return false;
            }
        }

        return true;
    }

    private static void HandleNetworkMessage(World w, byte[] payload)
    {
        using var reader = new PacketReader(payload);
        switch (reader.Message)
        {
            case NetMessage.ChatMessage:
            {
                var kind = (ChatKind)reader.ReadByte();
                var sender = reader.ReadString();
                var text = reader.ReadString();
                chatView.AddMessage(kind, sender, text);
                break;
            }
            case NetMessage.TimeSync:
                w.SetLightingTime(reader.ReadDouble());
                break;
            case NetMessage.PlayerList:
            {
                var count = reader.ReadInt();
                remoteScratch.Clear();
                for (var i = 0; i < count; i++)
                {
                    var id = reader.ReadInt();
                    var name = reader.ReadString();
                    var eye = reader.ReadVector3();
                    var yaw = reader.ReadFloat();
                    var pitch = reader.ReadFloat();
                    var flags = (NetPlayerFlags)reader.ReadByte();
                    var action = (NetPlayerAction)reader.ReadByte();
                    var actionSeq = reader.ReadByte();
                    remoteScratch.Add(new RemotePlayerSnapshot(id, name, eye, yaw, pitch, flags, action, actionSeq));
                }
                w.SyncRemotePlayers(remoteScratch, netPlayerId);
                break;
            }
            case NetMessage.BlockCorrection:
            {
                // The server's state of the cells around an intent's target, after judging it: it
                // overrides the prediction that intent made, but never a newer one
                var seq = reader.ReadInt();
                ReadCellChanges(reader);
                netApplyScratch.Clear();
                foreach (var change in netCellsScratch.Where(change => prediction.Offer(change, seq)))
                    netApplyScratch.Add(change);
                w.ApplyNetworkBlockChanges(netApplyScratch);
                break;
            }
            case NetMessage.IntentResult:
            {
                // The intent is settled either way: what was held back behind its prediction (the
                // newest value the server sent for those cells) lands now
                var seq = reader.ReadInt();
                var accepted = reader.ReadBool();
                netApplyScratch.Clear();
                prediction.Resolve(seq, netApplyScratch);
                w.ApplyNetworkBlockChanges(netApplyScratch);
                if (!accepted && debugEnabled)
                    Log.WriteLine($"Multiplayer: the server refused intent #{seq}");
                break;
            }
            case NetMessage.StationState:
                w.ApplyNetworkStations(reader);
                break;
            case NetMessage.PlayerStatus:
            {
                // The server owns this player's status: take the modes it granted, mirror its
                // health, let this client's physics apply a mob hit's shove, and ask to respawn
                // as soon as it reports the player dead
                var status = PlayerStatusCodec.Read(reader);
                w.Camera.SetModes(status.Modes.HasFlag(NetPlayerFlags.Creative),
                    status.Modes.HasFlag(NetPlayerFlags.Flying));
                var healthBefore = w.Health;
                w.ApplyNetworkHealth(status.Health);
                var knockback = status.Knockback;
                // AddKnockback normalizes the direction itself and keeps a lift-only hit's pop
                if (knockback.LengthSquared() > 0f || status.Lift > 0f)
                    w.Camera.AddKnockback(knockback, knockback.Length(), status.Lift);
                // A shove that came with lost health was a blow: the damage arc points back at it
                if (status.Health < healthBefore)
                    w.Camera.AddDamageDirection(knockback);
                if (status.Dead && !netDead)
                {
                    netDead = true;
                    SendRespawn(w.Camera);
                }
                else if (!status.Dead)
                {
                    netDead = false;
                }
                break;
            }
            case NetMessage.PlayerCorrection:
            {
                // The server put this player back (an illegal move, or a respawn): snap there and
                // ack it, at once, so the server takes positions again from the next state
                var seq = reader.ReadInt();
                var eye = reader.ReadVector3();
                if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z))
                    throw new System.IO.InvalidDataException("player correction eye is not finite");
                w.Camera.TestTeleport(eye, w.Camera.Yaw, w.Camera.Pitch);
                netCorrectionAck = seq;
                netStateTimer = WorldGenerationConfig.NetPlayerStateSeconds;
                break;
            }
            case NetMessage.ChunkData:
            {
                var cx = reader.ReadInt();
                var cy = reader.ReadInt();
                var cz = reader.ReadInt();
                clientStreamer?.ApplyChunk(cx, cy, cz, reader.ReadBytes());
                break;
            }
            case NetMessage.ColumnTerrain:
            {
                var cx = reader.ReadInt();
                var cz = reader.ReadInt();
                clientStreamer?.ApplyColumn(cx, cz, reader.ReadBytes());
                break;
            }
            case NetMessage.LodRegionData:
            {
                reader.ReadInt();
                reader.ReadInt();
                reader.ReadInt();
                clientLodStreamer?.Apply(reader.ReadBytes());
                break;
            }
            case NetMessage.EntityList:
            {
                var count = reader.ReadInt();
                netEntitiesScratch.Clear();
                for (var i = 0; i < count; i++)
                {
                    var kind = (NetEntityKind)reader.ReadByte();
                    var id = reader.ReadInt();
                    var position = reader.ReadVector3();
                    var yaw = reader.ReadFloat();
                    var health = reader.ReadInt();
                    var ammo = reader.ReadInt();
                    var itemId = reader.ReadByte();
                    var velocity = reader.ReadVector3();
                    netEntitiesScratch.Add(new NetEntity(kind, id, position, yaw, health, ammo, itemId, velocity));
                }
                w.ApplyNetworkEntities(netEntitiesScratch);
                break;
            }
            case NetMessage.BlockChanges:
            {
                // The server's own writes (anyone's edits, blasts, flow, decay); a cell this client
                // predicted waits until the server has answered that prediction
                ReadCellChanges(reader);
                netApplyScratch.Clear();
                foreach (var change in netCellsScratch.Where(change => prediction.Offer(change, null)))
                    netApplyScratch.Add(change);
                w.ApplyNetworkBlockChanges(netApplyScratch);
                break;
            }
            case NetMessage.InventoryState:
            {
                // A snapshot answering an older intent than the newest one that changed the
                // inventory here would undo that change for a moment: the newer answer follows
                var seq = reader.ReadInt();
                var data = reader.ReadBytes();
                if (prediction.AcceptsInventory(seq))
                    w.Inventory.Import(InventoryNetCodec.Decode(data));
                break;
            }
        }
    }

    /// <summary>Reads a count-prefixed batch of cell changes into <c>netCellsScratch</c>.</summary>
    private static void ReadCellChanges(PacketReader reader)
    {
        var count = reader.ReadInt();
        if (count < 0)
            throw new InvalidDataException($"cell batch of {count}");
        netCellsScratch.Clear();
        for (var i = 0; i < count; i++)
        {
            var x = reader.ReadInt();
            var y = reader.ReadInt();
            var z = reader.ReadInt();
            var blockId = reader.ReadByte();
            var state = reader.ReadByte();
            netCellsScratch.Add(new NetCellChange(x, y, z, blockId, state));
        }
    }

    /// <summary>Asks the server for one chunk (a streamed client loads no chunks itself).</summary>
    private static void SendChunkRequest(int cx, int cy, int cz)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.ChunkRequest);
        writer.Write(cx);
        writer.Write(cy);
        writer.Write(cz);
        client.Send(writer.ToArray());
    }

    /// <summary>Asks the server for one chunk column's terrain heights (the client's light seeding).</summary>
    private static void SendColumnRequest(int cx, int cz)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.ColumnRequest);
        writer.Write(cx);
        writer.Write(cz);
        client.Send(writer.ToArray());
    }

    /// <summary>Asks the server to build and send one far-LOD region (a client builds none itself).</summary>
    private static void SendLodRequest(int level, int rx, int rz)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.LodRegionRequest);
        writer.Write(level);
        writer.Write(rx);
        writer.Write(rz);
        client.Send(writer.ToArray());
    }

    /// <summary>Selects a hotbar slot: a server intent while connected, a local select otherwise.</summary>
    private static void RequestSelectHotbar(int index)
    {
        if (!SendInventoryIntent(InventoryIntent.Select(index)))
            world!.Inventory.Select(index);
    }

    /// <summary>
    /// The tooltip for whatever the cursor hovers while the [E] panel is open: a panel row's item
    /// (or the tool instance it names) or a hotbar slot's, with the cursor as the anchor. Null
    /// when nothing hovers.
    /// </summary>
    private static (Vector2 Anchor, List<string> Lines)? HoveredTooltip()
    {
        if (!inventoryView.IsOpen || pauseMenuView.IsOpen || inputManager?.Mouse == null) return null;
        var inventory = world?.Inventory;
        if (inventory == null || renderManager == null) return null;
        var position = new Vector2(inputManager.Mouse.Position.X, inputManager.Mouse.Position.Y);

        // A panel row first (the rows sit over the list), then a hotbar slot
        if (HoveredRow(out var rowId, out var rowTool))
        {
            var stack = rowTool != 0 ? inventory.GetToolStack(rowTool) : null;
            return (position, ItemTooltip.Lines(rowTool != 0 ? stack!.DefinitionId : rowId, stack));
        }

        if (renderManager.UiHotbarSlotAt(position) is not { } slot || slot < 0 || inventory.IdAt(slot) == 0)
            return null;
        {
            var tool = inventory.ToolInstanceAt(slot);
            var stack = tool != 0 ? inventory.GetToolStack(tool) : null;
            return (position, ItemTooltip.Lines(inventory.IdAt(slot), stack));
        }
    }

    /// <summary>
    /// The panel row under the cursor, if any: its item id, or the tool instance it names (then
    /// the id is 0). The one reading of "which row is hovered" the number keys and the tooltip
    /// share, so they never disagree.
    /// </summary>
    private static bool HoveredRow(out byte id, out int toolInstance)
    {
        id = 0;
        toolInstance = 0;
        var inventory = world?.Inventory;
        if (inventory == null || inputManager?.Mouse == null || renderManager == null) return false;
        if (!inventoryView.Search.IsEmpty) return false;
        if (inventoryView.Tab == InventoryCategory.Crafting) return false;
        if (world!.Camera.IsCreative) return false;

        var rowCount = RowCountFor(inventoryView.Tab);
        var offset = InventoryPanelLayout.ClampedOffset(inventoryView.ScrollOffset, rowCount,
            renderManager.UiCanvasSize);
        var row = renderManager.UiPanelRowAt(
            new Vector2(inputManager.Mouse.Position.X, inputManager.Mouse.Position.Y), offset, rowCount);
        if (row == null) return false;

        if (inventoryView.Tab == InventoryCategory.Tools)
        {
            var entries = inventory.ToolEntries();
            if (row.Value >= entries.Count) return false;
            toolInstance = entries[row.Value].InstanceId;
            return true;
        }

        var stacks = inventory.StackEntries(inventoryView.Tab);
        if (row.Value >= stacks.Count) return false;
        id = stacks[row.Value].Id;
        return true;
    }

    /// <summary>
    /// The number keys while the [E] panel is open: the row under the cursor goes into that hotbar
    /// slot, the same equip a click on the slot performs with the row selected. Only the plain
    /// tabs — a search result and a creative catalog row have their own click actions.
    /// </summary>
    private static void AssignHoveredRowToHotbar()
    {
        var inventory = world?.Inventory;
        if (inventory == null) return;
        if (!HoveredRow(out var id, out var tool)) return;
        if (id == 0 && tool == 0) return;

        for (var i = 0; i < NumberKeys.Length; i++)
        {
            if (inputManager!.Keyboard?.IsKeyPressed(NumberKeys[i]) != true) continue;
            var intent = tool != 0
                ? InventoryIntent.EquipTool(i, tool)
                : InventoryIntent.EquipStackable(i, id);
            if (SendInventoryIntent(intent)) return;
            if (tool != 0)
                inventory.EquipTool(i, tool);
            else
                inventory.EquipStackable(i, id);
            return;
        }
    }

    /// <summary>
    /// After a local selection change (the wheel), tells the server about it while connected,
    /// since the server owns the selection and its next inventory sync would otherwise revert the
    /// slot. Nothing to do in singleplayer.
    /// </summary>
    private static void MirrorSelection(int index) => SendInventoryIntent(InventoryIntent.Select(index));

    /// <summary>Throws the held item: a server intent while connected, a local throw otherwise.</summary>
    private static void RequestThrowSelected(bool wholeStack)
    {
        if (!SendInventoryIntent(InventoryIntent.Drop(wholeStack)))
            world!.ThrowSelected(wholeStack);
    }

    /// <summary>
    /// Sends one inventory intent while connected and returns whether it went out. The server
    /// replays it against this player's inventory there and answers with the snapshot this client
    /// then imports as the truth, so a local change made optimistically alongside the intent stays
    /// only if the server agreed. False (nothing sent) in singleplayer.
    /// </summary>
    private static bool SendInventoryIntent(InventoryIntent intent)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return false;
        using var writer = new PacketWriter(NetMessage.InventoryRequest);
        // Every inventory intent may change the inventory here, so snapshots answering an older
        // intent are dropped until this one's answer arrives
        writer.Write(prediction.Commit(predictsInventory: true));
        InventoryIntentCodec.Write(writer, intent);
        client.Send(writer.ToArray());
        return true;
    }

    /// <summary>A cell the world just changed as part of a predicted action (World.BlockEdited).</summary>
    private static void StagePredictedCell(int x, int y, int z, byte blockId, byte state) =>
        prediction.Stage(x, y, z);

    /// <summary>
    /// Sends one world intent (World.IntentRaised) with the next sequence id, which also tags the
    /// cells the action predicted and, for an intent whose prediction spends or credits items, the
    /// inventory. Dropped (the prediction is rubberbanded by nothing) when not connected.
    /// </summary>
    private static void SendWorldIntent(WorldIntent intent)
    {
        if (netClient is not { Status: NetworkStatus.Handshaken } client)
            return;
        using var writer = new PacketWriter(NetMessage.WorldIntent);
        WorldIntentCodec.Write(writer, prediction.Commit(intent.PredictsInventory), intent);
        client.Send(writer.ToArray());
    }

    /// <summary>
    /// Asks the server to respawn this (dead) player and restarts the retry clock. The server
    /// answers with a correction to the respawn point and a status that no longer reads dead.
    /// </summary>
    private static void SendRespawn(Camera camera)
    {
        netRespawnTimer = 0f;
        SendWorldIntent(WorldIntent.Respawn(camera.Position, camera.LastAimDirection));
    }

    /// <summary>Tears the connection down and returns to the server list with a notice.</summary>
    private static void DisconnectToMenu(string notice)
    {
        DisconnectNetwork();
        // A client world is storeless; dispose it so its workers stop before the menu.
        if (currentWorld == null && world != null)
        {
            var clientWorld = world;
            world = null;
            clientWorld.SaveAndDispose(renderManager);
        }
        ResetWorldViews();
        AbortConnect(notice);
    }

    private static void DisconnectNetwork()
    {
        netClient?.Dispose();
        netClient = null;
        clientStreamer = null;
        clientLodStreamer = null;
        netPlayerId = -1;
        netStateTimer = 0;
        netKeepAliveTimer = 0;
        netCorrectionAck = 0;
        netDead = false;
        netRespawnTimer = 0f;
        prediction = new ClientPrediction();
        if (world == null) return;
        world.ClearRemotePlayers();
        world.BlockEdited = null;
        world.IntentRaised = null;
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
            case PendingWork.Connect:
                BeginConnect();
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
        // A multiplayer client leaves the server and returns to the server list; a singleplayer
        // world saves and returns to the worlds page.
        var wasMultiplayer = netClient != null;
        DisconnectNetwork();
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
    private static void ResetWorldViews()
    {
        pauseMenuView.Page = PauseMenuPage.Closed;
        settingsMenuView.DragRow = null;
        inventoryView.ResetForNewWorld();
        chatView.Reset();
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

        switch (screen)
        {
            case AppScreen.Menu:
                FrameProfiler.Begin(ProfileSection.ProgramUpdate);
                SyncWindowSettings();
                TrackSettingsDrag();
                FrameProfiler.End(ProfileSection.ProgramUpdate);
                return;
            case AppScreen.Loading:
            {
                FrameProfiler.Begin(ProfileSection.ProgramUpdate);
                SyncWindowSettings();
                FrameProfiler.End(ProfileSection.ProgramUpdate);
                // Connecting to a server: no world exists yet; the handshake builds it.
                if (world == null)
                {
                    UpdateConnecting((float)deltaTime);
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
                LogDebugLine(deltaTime);
                FrameProfiler.End(ProfileSection.DebugLog);
                return;
            }
        }

        if (screen != AppScreen.Playing) return;
        var w = world!;
        switch (netClient)
        {
            // The server connection, if any: send our state and apply what it broadcast.
            case { Status: NetworkStatus.Handshaken } connectedClient when !UpdateNetwork(connectedClient, (float)deltaTime):
                return;
            case { Status: NetworkStatus.Disconnected or NetworkStatus.Failed } lostClient:
                lostClient.Dispose();
                DisconnectToMenu("Lost connection to the server");
                return;
        }
        // The loading overlay fades out over the first playing frames
        if (!loadingView.IsFinished)
            loadingView.Update(1f, true, (float)deltaTime);

        FrameProfiler.Begin(ProfileSection.ProgramUpdate);

        placeTimer = Math.Min(MAX_PLACE_TIMER, placeTimer + deltaTime);

        // T opens the chat: only from a clean play state, so it is never a hotkey while a panel,
        // the pause menu or the chat itself already owns the keyboard.
        var tPressed = inputManager.Keyboard?.IsKeyPressed(Key.T) == true;
        if (tPressed && !tWasPressed && !chatView.IsOpen && !pauseMenuView.IsOpen
            && !inventoryView.IsOpen)
            chatView.Open();
        tWasPressed = tPressed;

        var xPressed = inputManager.Keyboard?.IsKeyPressed(Key.X) == true;
        if (xPressed && !xWasPressed && !chatView.IsOpen && inventoryView.Focus == InventoryFocus.None)
        {
            debugEnabled = !debugEnabled;
            Log.WriteLine($"Debug {(debugEnabled ? "ON" : "OFF")}");
        }
        xWasPressed = xPressed;

        // F1 toggles the HUD (the screenshot path's hideHud); F2 saves a PNG beside the saves
        var f1Pressed = inputManager.Keyboard?.IsKeyPressed(Key.F1) == true;
        if (f1Pressed && !f1WasPressed)
            hudVisible = !hudVisible;
        f1WasPressed = f1Pressed;

        var f2Pressed = inputManager.Keyboard?.IsKeyPressed(Key.F2) == true;
        if (f2Pressed && !f2WasPressed)
            screenshotRequested = true;
        f2WasPressed = f2Pressed;

        // Tab lists who is online while it is held (a server session; the chat owns Tab otherwise)
        tabHeld = inputManager.Keyboard?.IsKeyPressed(Key.Tab) == true
                  && !chatView.IsOpen && !pauseMenuView.IsOpen && !inventoryView.IsOpen;

        // ESC toggles the pause menu instead of closing the game: first press opens it,
        // ESC on the settings page steps back to the main page, ESC there resumes the game.
        // An open [E] panel or chat is closed first so they never overlap the menu.
        var escPressed = inputManager.Keyboard?.IsKeyPressed(Key.Escape) == true;
        if (escPressed && !escWasPressed)
        {
            if (chatView.IsOpen)
                chatView.Close();
            else if (inventoryView.IsOpen)
                SetInventoryOpen(false);
            else switch (pauseMenuView.Page)
            {
                case PauseMenuPage.Closed:
                case PauseMenuPage.Settings:
                    SetPauseMenu(PauseMenuPage.Main);
                    break;
                default:
                    SetPauseMenu(PauseMenuPage.Closed);
                    break;
            }
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
        if (ePressed && !eWasPressed && !pauseMenuView.IsOpen && !chatView.IsOpen
            && inventoryView.Focus == InventoryFocus.None)
            SetInventoryOpen(!inventoryView.IsOpen);
        eWasPressed = ePressed;

        var qPressed = inputManager.Keyboard?.IsKeyPressed(Key.Q) == true;
        // With the panel open, the number keys put the hovered row into that hotbar slot instead
        // (the equip a click on the slot performs with the row selected)
        if (inventoryView.IsOpen && inventoryView.Focus == InventoryFocus.None && !pauseMenuView.IsOpen)
            AssignHoveredRowToHotbar();

        if (!inventoryView.IsOpen && !pauseMenuView.IsOpen && !chatView.IsOpen)
        {
            // 1-10 grabs the hotbar slot directly; scroll still walks it one step at a time
            for (var i = 0; i < NumberKeys.Length; i++)
                if (inputManager.Keyboard?.IsKeyPressed(NumberKeys[i]) == true)
                    RequestSelectHotbar(i);

            // One throw per press, so holding Q doesn't empty the slot; Ctrl+Q throws the stack
            if (qPressed && !qWasPressed)
            {
                var wholeStack = inputManager.Keyboard?.IsKeyPressed(Key.ControlLeft) == true
                    || inputManager.Keyboard?.IsKeyPressed(Key.ControlRight) == true;
                RequestThrowSelected(wholeStack);
            }
        }
        // The latch tracks the key every frame, gated or not: a Q typed into the chat or the search
        // field must not read as a fresh press the moment the field lets the keyboard go.
        qWasPressed = qPressed;
        FrameProfiler.End(ProfileSection.ProgramUpdate);

        // Right-click use/place does real work (raycast plus a synchronous remesh on bucket
        // pours and single-block placements), so it gets its own span instead of hiding in
        // the input shell. The block interaction always runs first, on the press edge and then
        // on the repeat timer; a throwable only starts charging when nothing claimed the click,
        // so a mudball cooks on a lit campfire instead of sailing over it. Hold to charge,
        // release to throw.
        var rightDown = inputManager.Mouse?.IsButtonPressed(MouseButton.Right) == true;
        var rightHeld = rightDown && !inventoryView.IsOpen && !pauseMenuView.IsOpen && !chatView.IsOpen;
        var rightPressed = rightHeld && !rightWasDown;
        switch (rightHeld)
        {
            case true when w.IsUseCharging:
                w.UpdateUseCharge((float)deltaTime);
                break;
            case true when w.IsThrowCharging:
                w.UpdateThrowCharge((float)deltaTime);
                break;
            case true when rightPressed || placeTimer >= MAX_PLACE_TIMER:
            {
                placeTimer = 0;
                FrameProfiler.Begin(ProfileSection.UsePlace);
                // A timed use (use.seconds) charges instead of firing on the click, and runs only
                // when the hold completes; anything else uses or places on the click
                var claimed = w.TryBeginUseCharge() || w.TryUseSelected(World.InteractionReach);
                FrameProfiler.End(ProfileSection.UsePlace);
                if (!claimed)
                    w.TryBeginThrowCharge();
                break;
            }
            case false when w.IsThrowCharging || w.IsUseCharging:
            {
                FrameProfiler.Begin(ProfileSection.UsePlace);
                if (w.IsUseCharging)
                    w.CancelUseCharge();
                else
                    w.ReleaseThrow();
                placeTimer = 0;
                FrameProfiler.End(ProfileSection.UsePlace);
                break;
            }
        }
        rightWasDown = rightDown;
        if (!rightHeld && (inventoryView.IsOpen || pauseMenuView.IsOpen || chatView.IsOpen))
        {
            if (w.IsThrowCharging) w.CancelThrowCharge();
            if (w.IsUseCharging) w.CancelUseCharge();
        }

        // The menu freezes the simulation: the world keeps rendering behind the overlay
        // but physics, chunks, fluids and mining all hold still until resume.
        // The update below times itself; the shell spans above and below stay outside it.
        if (!pauseMenuView.IsOpen)
        {
            // A focused text field owns the keyboard: pass no board so the camera cannot act on
            // the very keys being typed (E, C, Space, WASD) while the panel or chat is up.
            var board = (inventoryView.IsOpen && inventoryView.Focus != InventoryFocus.None)
                || chatView.IsOpen
                ? null
                : inputManager.Keyboard;
            w.Update((float)deltaTime, board);

            // The chat log fades its old lines while closed; its clock runs every playing frame
            chatView.Update((float)deltaTime);
            // A death posts one system line (singleplayer; a server broadcasts its own)
            if (w.TakeDeathMessage() is { } death)
                chatView.AddSystem(death);

            FrameProfiler.Begin(ProfileSection.Breaking);
            w.UpdateBlockBreaking(deltaTime,
                !inventoryView.IsOpen && !chatView.IsOpen
                && inputManager.Mouse?.IsButtonPressed(MouseButton.Left) == true);
            FrameProfiler.End(ProfileSection.Breaking);

            var middlePressed = !inventoryView.IsOpen && !chatView.IsOpen
                && inputManager.Mouse?.IsButtonPressed(MouseButton.Middle) == true;
            if (middlePressed && !middleWasPressed)
            {
                // A pick selects the slot already holding the block or equips it into the
                // selected one; while connected the inventory is the server's, so the pick is
                // also sent as an intent, or the next inventory sync would undo it.
                if (w.TryPickTargetedBlock(w.Inventory, out var pickedId))
                    SendInventoryIntent(InventoryIntent.PickBlock(pickedId));
            }
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
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
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
        inputManager.Mouse?.Cursor.CursorMode = open ? CursorMode.Normal : CursorMode.Disabled;
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

        if (GameSettings.MaxFpsIndex == appliedMaxFps) return;
        appliedMaxFps = GameSettings.MaxFpsIndex;
        var fps = GameSettings.MaxFpsOptions[
            Math.Clamp(appliedMaxFps, 0, GameSettings.MaxFpsOptions.Length - 1)];
        windowManager.Window.UpdatesPerSecond = fps;
        windowManager.Window.FramesPerSecond = fps;
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

    private static void HandleSettingsClick(Vector2 position)
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

    private static void HandleMenuClick(Vector2 position)
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
    private static void HandleInventoryScrollbarPress(Vector2 position)
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
                inventoryView.ThumbDragGrab = p.Y - (thumb.Center.Y - thumb.Size.Y / 2f);
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

    /// <summary>The keys the chat input reacts to while it is open. ESC is left to the polled
    /// handler, so closing chat and the pause menu never both fire on one press.</summary>
    private static void HandleChatKey(Key key)
    {
        switch (key)
        {
            case Key.Backspace:
                chatView.Backspace();
                break;
            case Key.Enter or Key.KeypadEnter:
                SubmitChat();
                break;
            case Key.Up:
                chatView.HistoryUp();
                break;
            case Key.Down:
                chatView.HistoryDown();
                break;
        }
    }

    private static void SubmitChat()
    {
        var text = chatView.Submit();
        if (string.IsNullOrEmpty(text)) return;
        // A command line runs locally in singleplayer and goes up to a server otherwise (which
        // runs it for that player); either way it is never echoed as chat
        if (text[0] == '/')
        {
            if (netClient is { Status: NetworkStatus.Handshaken })
                SendChat(text);
            else
                RunCommand(text);
            return;
        }
        SendChat(text);
    }

    /// <summary>The Tab player list's lines: the local player first, then the server's others.</summary>
    private static IReadOnlyList<string> PlayerListNames()
    {
        _playerNames.Clear();
        _playerNames.Add($"{chatView.LocalName} (you)");
        if (world == null) return _playerNames;
        foreach (var remote in world.RemotePlayers)
            _playerNames.Add(remote.Name);
        return _playerNames;
    }

    private static readonly List<string> _playerNames = [];

    /// <summary>
    /// Runs one chat command locally, on the same table a server uses (<see cref="ChatCommands"/>):
    /// <c>/tp</c> and <c>/give</c> need creative mode, <c>/time</c> and <c>/help</c> do not, and
    /// every answer is a system chat line.
    /// </summary>
    private static void RunCommand(string line)
    {
        if (world is not { } w) return;
        var parsed = ChatCommands.Parse(line);
        switch (parsed.Kind)
        {
            case ChatCommands.CommandKind.Time:
                if (parsed.Args.Length == 0 || ChatCommands.TimeSeconds(parsed.Args[0]) is not { } seconds)
                {
                    chatView.AddSystem("Usage: /time day|night|<seconds>");
                    return;
                }
                w.SetLightingTime(seconds);
                chatView.AddSystem($"Time set to {seconds:0.#} s");
                return;

            case ChatCommands.CommandKind.Teleport:
                if (!w.Camera.IsCreative)
                {
                    chatView.AddSystem("/tp needs creative mode");
                    return;
                }
                if (ChatCommands.TeleportTarget(parsed.Args) is not { } target)
                {
                    chatView.AddSystem("Usage: /tp x y z");
                    return;
                }
                w.Camera.TestTeleport(target, w.Camera.Yaw, w.Camera.Pitch);
                chatView.AddSystem($"Teleported to {target.X:0.#} {target.Y:0.#} {target.Z:0.#}");
                return;

            case ChatCommands.CommandKind.Give:
                if (!w.Camera.IsCreative)
                {
                    chatView.AddSystem("/give needs creative mode");
                    return;
                }
                if (ChatCommands.GiveRequest(parsed.Args) is not { } request)
                {
                    chatView.AddSystem("Usage: /give <item> [count]");
                    return;
                }
                if (!TryResolveContent(request.Item, out var id))
                {
                    chatView.AddSystem($"Unknown item '{request.Item}'");
                    return;
                }
                w.CreditItem(id, request.Count);
                chatView.AddSystem($"Gave {request.Count} x {request.Item}");
                return;

            default:
                chatView.AddSystem(ChatCommands.HelpText);
                return;
        }
    }

    // An item or block by plain name (blocks first, like a loot entry) or by its block//item/
    // pinned name
    private static bool TryResolveContent(string name, out byte id)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveBlock(trimmed[6..], out id);
        if (trimmed.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveItem(trimmed[5..], out id);
        return ContentRegistry.TryResolveBlock(trimmed, out id) || ContentRegistry.TryResolveItem(trimmed, out id);
    }

    /// <summary>Sends one chat line: a local echo in singleplayer, the server in multiplayer
    /// (which broadcasts it back to every client, so it is never echoed twice).</summary>
    private static void SendChat(string text)
    {
        if (netClient is { Status: NetworkStatus.Handshaken } client)
        {
            using var writer = new PacketWriter(NetMessage.ChatSend);
            writer.Write(text);
            client.Send(writer.ToArray());
            return;
        }

        chatView.AddMessage(ChatKind.Player, chatView.LocalName, text);
    }

    private static void MoveSearchHighlight(int delta)
    {
        if (inventoryView.Search.IsEmpty) return;
        var count = InventorySearch.Search(inventoryView.Search.Query, world!.Inventory, world.Camera.IsCreative).Count;
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
        var hits = InventorySearch.Search(inventoryView.Search.Query, world!.Inventory, world.Camera.IsCreative);
        if (hits.Count == 0) return;
        // The highlight is only clamped by the arrow keys; the list can shrink under it (an owned
        // item crafted away, a server inventory sync), so Enter falls back to the first hit.
        var highlight = inventoryView.Search.Highlight;
        if (highlight < 0 || highlight >= hits.Count)
            highlight = 0;
        ApplySearchHit(hits[highlight]);
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
        // The hotbar equip and the preview read the pick through the current tab, so a tool
        // picked from the Blocks tab's search has to land on the Tools tab to be equippable.
        inventoryView.Tab = hit.Category;
        if (hit.ToolInstance == 0 && inventoryView.Craft.Recipe != null)
            inventoryView.Craft.FeedFromInventory(hit.Id, world.Inventory);
    }

    /// <summary>Grants a catalog id to the player, popping the hotbar slot a fresh pickup lands
    /// in; the catalog's +1/+10/+100 buttons and the row body go through here.</summary>
    private static void GrantFromCreative(byte id, int amount)
    {
        var inventory = world!.Inventory;
        if (!inventory.Add(id, amount)) return;
        // Applied here at once; on a server only a player it lets be creative keeps it
        SendInventoryIntent(InventoryIntent.CreativeGrant(id, amount));
        if (Inventory.CategoryOf(id) is InventoryCategory.Blocks or InventoryCategory.Misc)
            inventoryView.SelectedId = id;
    }

    /// <summary>
    /// Acts on one creative catalog row click: a button grants its amount or clears the id's
    /// counts, the row body grants one.
    /// </summary>
    private static void ApplyCatalogButton(byte id, int row, float offset, int rowCount,
        Vector2 position)
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
                {
                    SendInventoryIntent(InventoryIntent.CreativeClear(id));
                    inventoryView.ClearSelection();
                }
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
    private static void HandleInventoryClick(Vector2 position)
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
        var hits = searching
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
            else switch (inventoryView.Tab)
            {
                case InventoryCategory.Tools:
                    inventoryView.SelectedToolInstance = inventory.ToolEntries()[row.Value].InstanceId;
                    inventoryView.SelectedId = 0;
                    break;
                case InventoryCategory.Crafting:
                    inventoryView.Craft.Select(ContentRegistry.CraftingRecipes[row.Value]);
                    // A newly opened recipe starts at the top of its card.
                    inventoryView.CraftScrollOffset = 0f;
                    break;
                default:
                {
                    var picked = inventory.StackEntries(inventoryView.Tab)[row.Value].Id;
                    inventoryView.SelectedId = picked;
                    inventoryView.SelectedToolInstance = 0;
                    if (inventoryView.Craft.Recipe != null)
                        inventoryView.Craft.FeedFromInventory(picked, inventory);
                    break;
                }
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
                {
                    // The fed members are read before the craft, which spends the reservation
                    var recipe = craft.Recipe;
                    var batch = craft.Batch;
                    var members = new byte[recipe?.Ingredients.Length ?? 0];
                    for (var i = 0; i < members.Length; i++)
                        members[i] = craft.FedAt(i).MemberId;
                    if (craft.Craft(inventory) && recipe != null)
                        SendInventoryIntent(InventoryIntent.Craft(recipe.Name, batch, members));
                    return;
                }
                case CraftHitKind.None:
                    break;
            }
        }

        // Equipped here at once and, while connected, sent as an intent: the server applies the
        // same equip to its copy (only for something this player owns), so the next sync keeps it.
        var hotbar = renderManager.UiHotbarSlotAt(position);
        if (!hotbar.HasValue) return;
        if (inventoryView.Tab == InventoryCategory.Tools && inventoryView.SelectedToolInstance != 0)
        {
            inventory.EquipTool(hotbar.Value, inventoryView.SelectedToolInstance);
            SendInventoryIntent(InventoryIntent.EquipTool(hotbar.Value, inventoryView.SelectedToolInstance));
        }
        else if (inventoryView.SelectedId != 0)
        {
            inventory.EquipStackable(hotbar.Value, inventoryView.SelectedId);
            SendInventoryIntent(InventoryIntent.EquipStackable(hotbar.Value, inventoryView.SelectedId));
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
        var hideHud = !hudVisible
            || (visualDirector?.TakeScreenshot == true && visualDirector.ScreenshotHideHud);
        if (!hideHud)
            world.RenderUI(renderManager, inventoryView,
                (float)world.LightingTime, shownFps, debugEnabled,
                showCoordinates: true, menu: pauseMenuView, settings: settingsMenuView);
        // The chat log and input draw over the HUD; hidden with the HUD and behind the pause menu.
        if (!hideHud && !pauseMenuView.IsOpen)
            renderManager.RenderChat(windowManager.Window, chatView, windowManager.Window.Time);
        // The Tab player list rides over the HUD while the key is held (a server session only)
        if (!hideHud && !pauseMenuView.IsOpen && tabHeld && netClient != null)
            renderManager.RenderPlayerList(windowManager.Window, PlayerListNames());
        // A hover tooltip over the [E] panel's rows and the hotbar slots
        if (!hideHud && !pauseMenuView.IsOpen)
            renderManager.RenderTooltip(windowManager.Window, HoveredTooltip());
        // The loading screen fades out over the world's first playing frames
        if (!loadingView.IsFinished)
            renderManager.RenderLoadingOverlay(windowManager.Window, loadingView, windowManager.Window.Time);
        renderManager.EndGpuPass(ProfileSection.UiPass);
        FrameProfiler.End(ProfileSection.UiPass);

        // Timed even when no screenshot is due, so the pairing stays obvious; an empty span
        // costs one Stopwatch read.
        FrameProfiler.Begin(ProfileSection.Screenshot);
        if (visualDirector?.TakeScreenshot == true || screenshotRequested)
        {
            var scripted = visualDirector?.TakeScreenshot == true;
            var path = scripted ? visualDirector!.ScreenshotPath : Path.Combine(ScreenshotPaths.Directory,
                ScreenshotPaths.NameFor(DateTime.Now));
            if (scripted) visualDirector!.TakeScreenshot = false;
            else screenshotRequested = false;
            var size = windowManager.Window.FramebufferSize;
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
        // A multiplayer client drops its connection; the server owns the save, not the client.
        DisconnectNetwork();
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