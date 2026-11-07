namespace VoxelEngine.Config;

/// <summary>
/// Multiplayer tuning: send cadences, the server's movement, edit and intent rules, socket
/// timeouts, far-LOD streaming and the creative default.
/// </summary>
public static class NetworkConfig
{
    // --- Multiplayer networking (NetProtocol, GameServer, NetworkClient) ---
    // The server broadcasts the player list on this cadence; clients send their own state at the
    // state cadence. The day clock syncs once a second, which is far below a visible sun step.
    public const double NetPlayerListSeconds = 0.1;
    public const double NetPlayerStateSeconds = 0.05;
    public const double NetTimeSyncSeconds = 1.0;
    // How often the server broadcasts the live mobs and drops.
    public const double NetEntitySyncSeconds = 0.1;
    // The largest speed the server accepts from a client (blocks/s) before clamping a state toward
    // it. Sprint flight moves PlayerPhysics.BaseSpeed * FlyBoostMultiplier = 8 * 10 = 80 b/s
    // horizontally and the same 80 b/s vertically (Space/Ctrl reuse the move speed), so the
    // flight diagonal is 80 * sqrt(2) ~ 113 b/s; 130 leaves slack for frame jitter.
    public const float NetMaxMoveBlocksPerSecond = 130f;
    // How far below a connected player's feet the server probes for ground (PlayerBodyProbe): a
    // body whose box lowered by this much collides is standing. It must exceed any clearance the
    // client's physics leaves under a landed body: BodyCollision moves in BoxSweep.StepSize (0.05)
    // steps and stops before the first colliding one, so a landing rests less than 0.05 above the
    // block top (StepClearance adds only 0.001 to a step-up), and gravity then settles it closer.
    public const float NetGroundProbeBlocks = 0.15f;
    // The movement rubberband (MovementRules), on top of the speed clamp above. The fastest a
    // state may climb without the flight flag, in blocks/s: a jump leaves the ground at
    // PlayerPhysics.JumpSpeed (11), and swimming up, the swim exit boost and a mob's knockback
    // lift are all slower, so only flight or a cheat climbs faster.
    public const float NetMaxRiseBlocksPerSecond = 16f;
    // Climb allowed in one state on top of that rate: a sprint auto-step of a whole block
    // (PlayerPhysics' SprintStepHeight) can land inside a single 50 ms state, plus jitter.
    public const float NetRiseSlackBlocks = 1.5f;
    // How long a player without the flight flag may stay airborne without sinking before the
    // server rubberbands it: longer than a full jump's climb and apex (about a third of a second
    // rising), so only a hover trips it.
    public const double NetHoverGraceSeconds = 1.0;
    // A state arriving more than this long after the previous one (a lag spike) is judged only by
    // the speed clamp and the collision test: the rise and hover rules need a short, honest gap.
    public const double NetMaxJudgedStateGapSeconds = 0.5;
    // How far from the player's eye a client may edit a block: the client's own raycast reach,
    // one block of cell-center slack, and one state interval of movement, since the eye the server
    // judges from can lag the eye that aimed the edit by up to one PlayerState.
    public const float NetMaxEditReachBlocks =
        World.InteractionReach + 1f + (float)(NetMaxMoveBlocksPerSecond * NetPlayerStateSeconds);
    // How far the eye a world intent claims may sit from the player's last accepted position: the
    // eye that aimed the action can run ahead of the server's copy by one state interval of
    // movement, plus the same slack a clamped move gets (NetValidation.AllowedDistance).
    public const float NetMaxIntentEyeDriftBlocks =
        (float)(NetMaxMoveBlocksPerSecond * NetPlayerStateSeconds) + 2f;
    // Edits (break completions, placements, right-click uses, throws) one player may make per
    // second, and how many may come in a burst above that before the server refuses them.
    public const float NetEditIntentsPerSecond = 20f;
    public const float NetEditIntentBurst = 20f;
    // Seconds by which a break completion may beat the server's own count of its hits: the start
    // can reach the server later than the completion, relative to when the client sent them.
    public const double NetBreakSlackSeconds = 0.1;
    // The same forgiveness for the pace between two attacks.
    public const double NetAttackSlackSeconds = 0.1;
    /// <summary>
    /// The network jitter in seconds the server forgives on a player's cast cooldown
    /// (<see cref="SpellConfig.CastCooldownSeconds"/>; a first guess).
    /// </summary>
    public const double NetCastSlackSeconds = 0.1;

    /// <summary>How far (blocks) a player's feet must rise between two accepted states, starting from the ground,
    /// for the server to count a jump (<c>ServerPlayer.ObserveAcceptedState</c>): above an auto-step's
    /// half block, under what a jump rises in one state.</summary>
    public const float NetJumpRiseBlocks = 0.2f;
    // A dead client sends a Respawn intent as soon as the server's status says it died, then again
    // this often while the status still reads dead (an earlier one refused, or lost to a race).
    public const double NetRespawnRetrySeconds = 1.0;
    // A creative client mines one block per this many seconds while the button is held, rather than
    // one per frame, so a held click stays inside the server's edit rate (singleplayer is unpaced).
    public const float ClientCreativeBreakSeconds = 0.1f;
    // How long a blocking send to one client may stall the server's main thread before the
    // connection is treated as dead (NetworkStream.WriteTimeout), in milliseconds.
    public const int NetSendTimeoutMs = 5000;
    // A connection (said hello or not) that sent nothing for this long is dropped. Clients send a
    // KeepAlive every NetKeepAliveSeconds and their PlayerState at 20 Hz, so this only fires for a
    // dead or stalled peer.
    public const double NetIdleTimeoutSeconds = 30.0;
    // Far-LOD regions the server builds per Pump. A joining client asks for hundreds; building
    // them all inside one message handler stalled the tick for seconds.
    public const int NetLodBuildsPerPump = 4;
    // Far-LOD region requests a client keeps outstanding at once, and how long before an
    // unanswered one is asked again (TCP is reliable: the resend only covers a server that
    // dropped the request, e.g. because its asker disconnected meanwhile).
    public const int NetLodInFlightMax = 64;
    public const double NetLodResendSeconds = 15.0;
    // How long a shutdown signal (SIGTERM, SIGHUP, process exit) waits for the server's main loop
    // to save the world before the handler gives up and lets the process end.
    public const double ServerShutdownWaitSeconds = 30.0;
    // Whether a dedicated server lets players be creative by default (GameServer.AllowCreative;
    // the server's --allow-creative argument turns it on). Off: every player stays survival and
    // creative grants are refused whatever the client's C toggle says.
    public const bool ServerAllowCreative = false;
    // How long a connect attempt may hang before the client gives up and returns to the menu.
    public const double NetConnectTimeoutSeconds = 10.0;
    // Sent roughly this often so an idle connection is not mistaken for a dead one.
    public const double NetKeepAliveSeconds = 5.0;
    // Largest squash/distance a remote player model is drawn from before it is culled.
    public const float NetRemotePlayerReachBlocks = 4000f;
}
