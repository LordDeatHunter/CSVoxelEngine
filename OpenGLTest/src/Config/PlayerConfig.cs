namespace VoxelEngine.Config;

/// <summary>
/// Player tuning: the break overlay, the camera and swing feel (sprint FOV, strafe tilt, hit-stop,
/// throw zoom), the water depth states, charged throws, the slime block bounce and the bandage.
/// </summary>
public static class PlayerConfig
{
    // --- Mining ---
    // The block-breaking crack sprite (assets/textures/environment/break_progress.png) is a
    // vertical strip of this many square frames, top frame first and blank (0 % damage). The
    // progress 0..1 picks the frame; uBreakProgress's alpha is only the crack shape, so the
    // opacity below is what keeps the black cracks from fully hiding the block underneath.
    public const int BreakOverlayFrames = 8;
    public const float BreakOverlayOpacity = 0.6f;

    // --- Camera and swing feel (Camera, SprintFov, StrafeTilt, SwingHitStop) ---
    // Degrees the view widens by at a full on-foot sprint, added to the base FOV and the punch
    // pinch. The 10x flight sprint widens exactly as far, never further (SprintFov clamps the
    // speed ramp at the on-foot sprint speed).
    public const float SprintFovExtraDegrees = 5f;
    // How fast the widening eases in and back out (MathUtils.Damp rate, per second)
    public const float SprintFovEaseRate = 6f;
    // Degrees the view rolls toward a full sideways strafe (a lean into the move), added to the run
    // sway, the hit kick and the death roll. Off while flying and while dead.
    public const float StrafeTiltDegrees = 1.5f;
    // How fast the tilt eases in and back to level (MathUtils.Damp rate, per second)
    public const float StrafeTiltEaseRate = 8f;
    // Hit-stop: a punch or slice that connects holds the swing (and the punch pinch) at its impact
    // pose for a beat, a light swing for HitStopSeconds and the heaviest punch for
    // HitStopHeavySeconds, never past HitStopMaxSeconds. Cosmetic only: the world, the mobs, their
    // recoil, the player's movement and attack pacing never stop.
    public const float HitStopSeconds = 0.15f;
    public const float HitStopHeavySeconds = 0.2f;
    public const float HitStopMaxSeconds = 0.25f;
    // The most of a swing's remaining post-impact time a hold may take. The rest of the swing replays
    // faster after the hold, so a frozen swing still ends exactly when it would have and nothing that
    // waits on it (the slice combo's next swing, the next jab) is ever delayed. A punch, whose
    // follow-through is short, therefore holds for less than HitStopSeconds.
    public const float HitStopMaxRecoveryShare = 0.7f;
    // Creative-flight pose (FlightPose): the legs trail back and bend, and the torso leans, up to the
    // caps below at FlightFullSpeed blocks per second (the 10x flight sprint is 80, so it holds the caps
    // from half of it); the legs also sway out of step, a little (FlightHoverSway of the full sway)
    // even when hovering
    public const float FlightFullSpeed = 40f;
    public const float FlightBlendRate = 6f;
    public const float FlightSpeedEaseRate = 5f;
    public const float FlightTrailHip = 0.5f;
    public const float FlightTrailKnee = 0.9f;
    public const float FlightRestKnee = 0.12f;
    public const float FlightLean = 0.45f;
    public const float FlightSwayHip = 0.2f;
    public const float FlightSwayKnee = 0.2f;
    public const float FlightHoverSway = 0.35f;
    public const float FlightSwayRate = 2.6f;

    // An axe's chop (SwordSwing.AxeChop) lasts the axe's hit gap, held between these (seconds)
    public const float AxeChopMinSeconds = 0.25f;
    // A creative player holding the mine button breaks the next block only this many seconds after
    // the last one (the press itself breaks at once), so a tap never takes the block behind it.
    public const float CreativeBreakRepeatSeconds = 0.2f;
    public const float AxeChopMaxSeconds = 0.8f;

    // Break kick (BreakKick): a landed mining hit on a block harder than the min nudges the view,
    // rising to the max strength at the full hardness (a trunk, an ore); the hit that breaks the
    // block kicks the final factor harder
    public const float BreakKickMinHardness = 6f;
    public const float BreakKickFullHardness = 24f;
    public const float BreakKickMaxStrength = 0.25f;
    public const float BreakKickFinalFactor = 1.6f;

    // Throw charge zoom (ThrowZoom): degrees the view narrows by at a full throw charge, added to the
    // base FOV, the sprint widening and the punch pinch. A spring follows the charge and springs back
    // on release; its rate in Hz and damping ratio (below 1 overshoots a touch wider on the release).
    public const float ThrowZoomDegrees = 4f;
    public const float ThrowZoomSpringHz = 2.2f;
    public const float ThrowZoomSpringDamping = 0.55f;

    // --- Water depth states (WaterImmersion, PlayerPhysics) ---
    // The share of the body (PlayerHeight) under the fluid surface from which a body with its eye
    // still above it floats rather than wades; the eye going under is always a swim.
    public const float WaterFloatShare = 0.5f;
    // Wading (under WaterFloatShare) walks, at this share of the walk speed: a lighter slowdown than
    // swimming's, with the normal jump.
    public const float WadeSpeedFactor = 0.75f;
    // Lava drags far harder than water: any part of the body in it scales the walk and the swim
    // stroke to this share of their already slowed speed, so sprinting through a dip cannot carry a run.
    public const float LavaSpeedFactor = 0.3f;
    // Floating: a body in deep water with its eye above the surface (and one that has not dived,
    // even with the eye under) settles where this share of it is under. Its vertical speed eases at
    // FloatSettleRate (per second) toward FloatBuoyancy (per second) times its height error, capped
    // by the swim sink and rise speeds; the pair is near critically damped, so it settles without
    // bobbing through the surface.
    public const float WaterFloatRestShare = 0.7f;
    public const float FloatBuoyancy = 2.5f;
    public const float FloatSettleRate = 6.4f;

    // --- Charged throws (ThrownProjectile) ---
    // Right-click in the air charges a throwable, release throws it. Damage scales from half
    // at a tap to full at a full charge, and the launch speed scales the same way.
    public const float ThrowChargeSeconds = 0.9f;
    public const float ThrowMinCharge = 0.5f;
    public const float ThrowSpeed = 40f;
    public const float ThrowLifetimeSeconds = 5f;

    // --- Thrown drops: a player's Q-drop or a landed throw waits this long before anyone can
    // collect it, so the thrower does not snap it straight back up. Mined and mob drops keep the
    // short ItemDrop grace. ---
    public const float ThrownPickupDelaySeconds = 2f;

    // --- Slime block (the block "bounce" flag): a body that lands on one is thrown back up at
    // this share of its impact and bills no fall damage; below the minimum it just rests ---
    public const float SlimeBlockBounceRestitution = 0.8f;
    public const float SlimeBlockBounceMinSpeed = 5f;

    // --- Sink blocks (the block "sink" flag, SinkLayer): blocks per second a player body standing
    // in a sink block's soft top layer descends until it rests on the solid part ---
    public const float SinkBlockSpeed = 0.6f;

    // --- Bandage (ItemUseAction.Heal): HP one restores, spent from the stack ---
    public const int BandageHealAmount = 20;

    // --- Drowning (Breath): air with the eye under water, refilled once it is out; at zero air a
    // blow lands every tick ---
    public const float AirSeconds = 15f;
    public const float AirRefillPerSecond = 10f;
    public const float DrownTickSeconds = 1f;
    public const int DrownDamagePerTick = 10;

    // --- Passive regeneration: out of combat (no damage taken or dealt for RegenDelaySeconds) the
    // pool refills; sprinting slows it by RegenSprintFactor (0 would halt it) ---
    public const float RegenDelaySeconds = 8f;
    public const float RegenPerSecond = 1.5f;
    public const float RegenSprintFactor = 0.25f;

    // --- Armor (ArmorRules): a blow keeps at least this much through any armor, by what dealt it; a source
    // with no entry keeps GearConfig.UniversalMinDamage. Drowning and magic (burning, frostburn) ignore armor. ---
    public const int ArmorMinDamageMob = 2;
    public const int ArmorMinDamageFall = 3;
    public const int ArmorMinDamageLava = 3;
    public const int ArmorMinDamageCactus = 1;
}
