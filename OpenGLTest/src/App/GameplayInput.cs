using System;
using Silk.NET.Input;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>A playing world's per-frame polled input, in frame order: the chat, debug, HUD, screenshot, player-list, ESC and [E] keys, the hotbar keys and throws, the right-click use/place/charge chain, then (unless paused) the world update, the chat clock, mining and the middle-click pick. Owns the input edges and the right-click repeat timer. Owned by Program; main thread only.</summary>
internal sealed class GameplayInput
{
    private readonly InputManager inputManager;
    private readonly RenderManager renderManager;
    private readonly InventoryView inventoryView;
    private readonly ChatView chatView;
    private readonly PauseMenuView pauseMenuView;
    private readonly InventoryPanelInput panelInput;
    private readonly MenuInput menuInput;
    private readonly ClientSession session;
    private readonly DevDiagnostics diagnostics;

    private bool hudVisible = true;
    private bool screenshotRequested;
    private bool f1WasPressed;
    private bool f2WasPressed;
    private bool tabHeld;
    private bool xWasPressed;
    private bool qWasPressed;
    private bool rightWasDown;
    private bool middleWasPressed;
    private bool eWasPressed;
    private bool tWasPressed;
    private bool escWasPressed;

    // TODO: move this to player class
    private static double placeTimer = MAX_PLACE_TIMER;
    private static readonly double MAX_PLACE_TIMER = 0.2;

    internal GameplayInput(InputManager inputManager, RenderManager renderManager, InventoryView inventoryView, ChatView chatView, PauseMenuView pauseMenuView, InventoryPanelInput panelInput, MenuInput menuInput, ClientSession session, DevDiagnostics diagnostics)
    {
        this.inputManager = inputManager;
        this.renderManager = renderManager;
        this.inventoryView = inventoryView;
        this.chatView = chatView;
        this.pauseMenuView = pauseMenuView;
        this.panelInput = panelInput;
        this.menuInput = menuInput;
        this.session = session;
        this.diagnostics = diagnostics;
    }

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>Whether the HUD draws (F1).</summary>
    internal bool HudVisible => hudVisible;

    /// <summary>An F2 screenshot waiting for the next rendered frame, which clears it.</summary>
    internal bool ScreenshotRequested { get => screenshotRequested; set => screenshotRequested = value; }

    /// <summary>Whether Tab holds the player list up this frame.</summary>
    internal bool TabHeld => tabHeld;

    /// <summary>Drops the input edges and the repeat timer a world switch must not carry over (<see cref="Program"/>'s ResetWorldViews).</summary>
    internal void ResetEdges()
    {
        escWasPressed = eWasPressed = qWasPressed = rightWasDown = middleWasPressed = false;
        placeTimer = MAX_PLACE_TIMER;
    }

    /// <summary>A right-button release: the next press uses or places at once.</summary>
    internal void ResetPlaceTimer() => placeTimer = MAX_PLACE_TIMER;

    /// <summary>Selects a hotbar slot: a server intent while connected, a local select otherwise.</summary>
    private void RequestSelectHotbar(int index)
    {
        if (!session.SendInventoryIntent(InventoryIntent.Select(index)))
            ActiveWorld!.Inventory.Select(index);
    }

    /// <summary>
    /// After a local selection change (the wheel), tells the server about it while connected,
    /// since the server owns the selection and its next inventory sync would otherwise revert the
    /// slot. Nothing to do in singleplayer.
    /// </summary>
    internal void MirrorSelection(int index) => session.SendInventoryIntent(InventoryIntent.Select(index));

    /// <summary>Throws the held item: a server intent while connected, a local throw otherwise.</summary>
    private void RequestThrowSelected(bool wholeStack)
    {
        if (!session.SendInventoryIntent(InventoryIntent.Drop(wholeStack)))
            ActiveWorld!.Throws.ThrowSelected(wholeStack);
    }

    /// <summary>The playing frame's input after the network pump: from the input shell through the pause-gated world update. <paramref name="w"/> is the playing world.</summary>
    internal void Update(World w, double deltaTime)
    {
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
            diagnostics.DebugEnabled = !diagnostics.DebugEnabled;
            Log.WriteLine($"Debug {(diagnostics.DebugEnabled ? "ON" : "OFF")}");
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
                panelInput.SetInventoryOpen(false);
            else switch (pauseMenuView.Page)
            {
                case PauseMenuPage.Closed:
                case PauseMenuPage.Settings:
                    menuInput.SetPauseMenu(PauseMenuPage.Main);
                    break;
                default:
                    menuInput.SetPauseMenu(PauseMenuPage.Closed);
                    break;
            }
        }
        escWasPressed = escPressed;

        menuInput.SyncWindowSettings();
        menuInput.TrackSettingsDrag();

        // A scrollbar thumb drag tracks the cursor every frame; only MouseUp (or closing the
        // panel) ends it, so there is deliberately no button-released branch here.
        if (inventoryView.IsOpen && inventoryView.ThumbDragGrab is { } grab && inputManager.Mouse != null)
        {
            var canvas = renderManager.Ui.ScreenSize;
            var p = renderManager.Ui.ToCanvas(inputManager.Mouse.Position);
            var value = InventoryPanelLayout.OffsetForThumbTop(
                p.Y - grab, inventoryView.ThumbDragRowCount, canvas);
            panelInput.SetListScroll(value, !inventoryView.Search.IsEmpty);
        }

        // The [E] panel owns the mouse: clicks equip, hotbar keys rest, and the world runs on.
        // While a text field has focus, E is typing, not a close.
        var ePressed = inputManager.Keyboard?.IsKeyPressed(Key.E) == true;
        if (ePressed && !eWasPressed && !pauseMenuView.IsOpen && !chatView.IsOpen
            && inventoryView.Focus == InventoryFocus.None)
            panelInput.SetInventoryOpen(!inventoryView.IsOpen);
        eWasPressed = ePressed;

        var qPressed = inputManager.Keyboard?.IsKeyPressed(Key.Q) == true;
        // With the panel open, the number keys put the hovered row into that hotbar slot instead
        // (the equip a click on the slot performs with the row selected)
        if (inventoryView.IsOpen && inventoryView.Focus == InventoryFocus.None && !pauseMenuView.IsOpen)
            panelInput.AssignHoveredRowToHotbar();

        if (!inventoryView.IsOpen && !pauseMenuView.IsOpen && !chatView.IsOpen)
        {
            // 1-10 grabs the hotbar slot directly; scroll still walks it one step at a time
            for (var i = 0; i < InventoryPanelInput.NumberKeys.Length; i++)
                if (inputManager.Keyboard?.IsKeyPressed(InventoryPanelInput.NumberKeys[i]) == true)
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
            case true when w.RightClick.IsUseCharging:
                w.RightClick.UpdateUseCharge((float)deltaTime);
                break;
            case true when w.Throws.IsThrowCharging:
                w.Throws.UpdateThrowCharge((float)deltaTime);
                break;
            case true when rightPressed || placeTimer >= MAX_PLACE_TIMER:
            {
                placeTimer = 0;
                FrameProfiler.Begin(ProfileSection.UsePlace);
                // A timed use (use.seconds) charges instead of firing on the click, and runs only
                // when the hold completes; anything else uses or places on the click
                var claimed = w.RightClick.TryBeginUseCharge() || w.RightClick.TryUseSelected(World.InteractionReach);
                FrameProfiler.End(ProfileSection.UsePlace);
                if (!claimed)
                    w.Throws.TryBeginThrowCharge();
                break;
            }
            case false when w.Throws.IsThrowCharging || w.RightClick.IsUseCharging:
            {
                FrameProfiler.Begin(ProfileSection.UsePlace);
                if (w.RightClick.IsUseCharging)
                    w.RightClick.CancelUseCharge();
                else
                    w.Throws.ReleaseThrow();
                placeTimer = 0;
                FrameProfiler.End(ProfileSection.UsePlace);
                break;
            }
        }
        rightWasDown = rightDown;
        if (!rightHeld && (inventoryView.IsOpen || pauseMenuView.IsOpen || chatView.IsOpen))
        {
            if (w.Throws.IsThrowCharging) w.Throws.CancelThrowCharge();
            if (w.RightClick.IsUseCharging) w.RightClick.CancelUseCharge();
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
            if (w.LocalPlayer.TakeDeathMessage() is { } death)
                chatView.AddSystem(death);

            FrameProfiler.Begin(ProfileSection.Breaking);
            w.LeftClick.UpdateBlockBreaking(deltaTime,
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
                if (w.Placer.TryPickTargetedBlock(w.Inventory, out var pickedId))
                    session.SendInventoryIntent(InventoryIntent.PickBlock(pickedId));
            }
            middleWasPressed = middlePressed;
        }
        else
        {
            middleWasPressed = false;
        }
    }
}
